using System.Globalization;
using System.Runtime.InteropServices;
using CorelSignStudio.Domain;

namespace CorelSignStudio.Corel;

public sealed class CorelAutomationService : ICorelAutomationService
{
    public const string DefaultProgId = "CorelDRAW.Application.27";

    private const int CdrMillimeter = 3;
    private const int CdrFilterCdr = 1795;

    private readonly StaThreadDispatcher _dispatcher = new("Corel Sign Studio COM STA");
    private readonly IDesignAssetResolver? _assetResolver;
    private readonly Action<string>? _log;
    private readonly string _progId;

    private object? _application;
    private object? _document;
    private bool _ownsApplication;
    private int _disposed;

    public CorelAutomationService(
        IDesignAssetResolver? assetResolver = null,
        Action<string>? log = null,
        string progId = DefaultProgId)
    {
        _assetResolver = assetResolver;
        _log = log;
        _progId = progId;
    }

    public Task<CorelConnectionInfo> ConnectAsync(bool visible = false, CancellationToken cancellationToken = default) =>
        ExecuteAsync("Connect", () =>
        {
            if (_application is null)
            {
                var applicationType = Type.GetTypeFromProgID(_progId, throwOnError: false)
                    ?? throw new InvalidOperationException($"COM ProgID '{_progId}' is not registered.");

                _application = TryGetRunningInstance(applicationType.GUID);
                if (_application is null)
                {
                    _application = Activator.CreateInstance(applicationType)
                        ?? throw new InvalidOperationException($"COM ProgID '{_progId}' returned no application instance.");
                    _ownsApplication = true;
                }
                else
                {
                    _ownsApplication = false;
                    WriteLog($"Attached to the running {_progId} instance through the Running Object Table.");
                }
            }

            dynamic application = _application;
            application.Visible = visible;

            var version = ReadVersion(application);
            var result = new CorelConnectionInfo(
                _progId,
                version,
                Environment.CurrentManagedThreadId,
                Thread.CurrentThread.GetApartmentState());

            WriteLog($"Connected to {_progId}; version={version}; thread={result.StaManagedThreadId}; apartment={result.ApartmentState}.");
            return result;
        }, cancellationToken);

    public Task RenderDesignAsync(DesignSpec design, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(design);
        design.Validate();

        return ExecuteAsync("RenderDesign", () =>
        {
            EnsureConnected();
            CloseDocumentCore();

            dynamic application = _application!;
            _document = application.CreateDocument();
            dynamic document = _document;
            document.Unit = CdrMillimeter;

            dynamic page = document.ActivePage;
            dynamic layer = document.ActiveLayer;
            try
            {
                page.SetSize(design.WidthMm, design.HeightMm);
                foreach (var element in design.Elements.Where(element => element.Visible).OrderBy(element => element.ZIndex))
                {
                    RenderElement(layer, document, design.HeightMm, element);
                }
            }
            finally
            {
                ReleaseComObject(layer);
                ReleaseComObject(page);
            }

            WriteLog($"Rendered {design.Elements.Count} element(s) on a {design.WidthMm} x {design.HeightMm} mm page.");
            return true;
        }, cancellationToken);
    }

    public Task<CorelSaveResult> SaveCdrAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = PrepareOutputPath(path, ".cdr");
        return ExecuteAsync("SaveCdr", () =>
        {
            dynamic document = EnsureDocument();
            var signature = ComDispatchInspector.GetMethodSignature(_document!, "SaveAs");
            WriteLog($"Runtime SaveAs signature: {signature}.");
            object? saveOptions = null;
            try
            {
                dynamic application = _application!;
                saveOptions = application.CreateStructSaveAsOptions();
                dynamic options = saveOptions;
                options.Filter = CdrFilterCdr;
                options.Overwrite = true;
                document.SaveAs(fullPath, options);
            }
            finally
            {
                ReleaseComObject(saveOptions);
            }

            const string invocationShape = "SaveAs(string, StructSaveAsOptions { Filter=cdrCDR, Overwrite=true })";

            if (!File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
            {
                throw new IOException($"CorelDRAW returned from SaveAs but did not create a non-empty file at '{fullPath}'.");
            }

            WriteLog($"Saved CDR: {fullPath}; runtime signature={signature}; invocation={invocationShape}.");
            return new CorelSaveResult(fullPath, signature, invocationShape);
        }, cancellationToken);
    }

    public Task ExportPdfAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = PrepareOutputPath(path, ".pdf");
        return ExecuteAsync("ExportPdf", () =>
        {
            dynamic document = EnsureDocument();
            document.PublishToPDF(fullPath);

            if (!File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
            {
                throw new IOException($"CorelDRAW returned from PublishToPDF but did not create a non-empty file at '{fullPath}'.");
            }

            WriteLog($"Exported PDF: {fullPath}.");
            return true;
        }, cancellationToken);
    }

    public Task<CorelDocumentInfo> OpenCdrAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        return ExecuteAsync("OpenCdr", () =>
        {
            EnsureConnected();
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("CDR file was not found.", fullPath);
            }

            CloseDocumentCore();
            dynamic application = _application!;
            _document = application.OpenDocument(fullPath);
            dynamic document = _document;
            document.Unit = CdrMillimeter;
            dynamic page = document.ActivePage;
            try
            {
                var result = new CorelDocumentInfo(
                    fullPath,
                    Convert.ToDouble(page.SizeWidth, CultureInfo.InvariantCulture),
                    Convert.ToDouble(page.SizeHeight, CultureInfo.InvariantCulture));
                WriteLog($"Reopened CDR: {fullPath}; page={result.WidthMm} x {result.HeightMm} mm.");
                return result;
            }
            finally
            {
                ReleaseComObject(page);
            }
        }, cancellationToken);
    }

    public Task CloseAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("Close", () =>
        {
            CloseDocumentCore();

            if (_application is not null)
            {
                try
                {
                    if (_ownsApplication)
                    {
                        dynamic application = _application;
                        application.Quit();
                    }
                }
                finally
                {
                    ReleaseComObject(_application);
                    _application = null;
                    _ownsApplication = false;
                }
            }

            WriteLog("CorelDRAW automation session closed.");
            return true;
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                CloseDocumentCore();
                if (_application is not null)
                {
                    try
                    {
                        if (_ownsApplication)
                        {
                            dynamic application = _application;
                            application.Quit();
                        }
                    }
                    finally
                    {
                        ReleaseComObject(_application);
                        _application = null;
                        _ownsApplication = false;
                    }
                }

                return true;
            }).ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Task<T> ExecuteAsync<T>(string operation, Func<T> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _dispatcher.InvokeAsync(() =>
        {
            try
            {
                return action();
            }
            catch (CorelAutomationException)
            {
                throw;
            }
            catch (Exception exception)
            {
                WriteLog($"{operation} failed: {exception}");
                throw new CorelAutomationException(operation, exception);
            }
        }, cancellationToken);
    }

    private void RenderElement(dynamic layer, dynamic document, double pageHeightMm, DesignElement element)
    {
        object? shape = null;
        try
        {
            shape = element switch
            {
                RectangleElement rectangle => layer.CreateRectangle2(
                    rectangle.XMm,
                    ToCorelY(pageHeightMm, rectangle.YMm),
                    rectangle.WidthMm,
                    rectangle.HeightMm,
                    rectangle.CornerRadiusMm,
                    rectangle.CornerRadiusMm,
                    rectangle.CornerRadiusMm,
                    rectangle.CornerRadiusMm),

                EllipseElement ellipse => layer.CreateEllipse(
                    ellipse.XMm,
                    ToCorelY(pageHeightMm, ellipse.YMm),
                    ellipse.XMm + ellipse.WidthMm,
                    ToCorelY(pageHeightMm, ellipse.YMm + ellipse.HeightMm)),

                LineElement line => layer.CreateLineSegment(
                    line.XMm,
                    ToCorelY(pageHeightMm, line.YMm),
                    line.XMm + line.WidthMm,
                    ToCorelY(pageHeightMm, line.YMm + line.HeightMm)),

                TextElement text => CreateText(layer, pageHeightMm, text),

                SvgElement svg => ImportAsset(layer, document, pageHeightMm, svg.AssetKey, svg.XMm, svg.YMm, svg.WidthMm, svg.HeightMm),
                ImageElement image => ImportAsset(layer, document, pageHeightMm, image.AssetKey, image.XMm, image.YMm, image.WidthMm, image.HeightMm),
                _ => throw new NotSupportedException($"Element type '{element.GetType().Name}' is not supported."),
            };

            ApplyStyle(shape, element);
            if (element.RotationDegrees != 0)
            {
                ((dynamic)shape).Rotate(element.RotationDegrees);
            }
        }
        finally
        {
            ReleaseComObject(shape);
        }
    }

    private object ImportAsset(
        dynamic layer,
        dynamic document,
        double pageHeightMm,
        string assetKey,
        double xMm,
        double yMm,
        double widthMm,
        double heightMm)
    {
        if (_assetResolver is null)
        {
            throw new InvalidOperationException("An IDesignAssetResolver is required to render SVG or image elements.");
        }

        var sourcePath = _assetResolver.ResolveAssetPath(assetKey);
        object? importFilter = null;
        try
        {
            importFilter = layer.Import(sourcePath);
            ((dynamic)importFilter).Finish();
        }
        finally
        {
            ReleaseComObject(importFilter);
        }

        dynamic shape = document.ActiveShape;
        var currentWidth = Convert.ToDouble(shape.SizeWidth, CultureInfo.InvariantCulture);
        var currentHeight = Convert.ToDouble(shape.SizeHeight, CultureInfo.InvariantCulture);
        if (currentWidth <= 0 || currentHeight <= 0)
        {
            throw new InvalidOperationException($"Imported asset '{assetKey}' has invalid bounds.");
        }

        var fitScale = Math.Min(widthMm / currentWidth, heightMm / currentHeight);
        shape.SetSize(currentWidth * fitScale, currentHeight * fitScale);
        shape.CenterX = xMm + (widthMm / 2);
        shape.CenterY = ToCorelY(pageHeightMm, yMm + (heightMm / 2));
        return shape;
    }

    private static object CreateText(dynamic layer, double pageHeightMm, TextElement text)
    {
        dynamic shape = layer.CreateArtisticText(
            0d,
            0d,
            text.Text,
            0,
            0,
            text.FontFamily,
            (float)text.FontSizePt,
            text.FontWeight is TextFontWeight.Bold or TextFontWeight.Black ? -1 : 0);

        var shapeWidth = Convert.ToDouble(shape.SizeWidth, CultureInfo.InvariantCulture);
        var shapeHeight = Convert.ToDouble(shape.SizeHeight, CultureInfo.InvariantCulture);
        if (shapeWidth > text.WidthMm || shapeHeight > text.HeightMm)
        {
            var fitScale = Math.Min(text.WidthMm / shapeWidth, text.HeightMm / shapeHeight);
            shape.SetSize(shapeWidth * fitScale, shapeHeight * fitScale);
            shapeWidth *= fitScale;
            shapeHeight *= fitScale;
        }

        shape.CenterX = text.HorizontalAlignment switch
        {
            ElementHorizontalAlignment.Left => text.XMm + (shapeWidth / 2),
            ElementHorizontalAlignment.Right => text.XMm + text.WidthMm - (shapeWidth / 2),
            _ => text.XMm + (text.WidthMm / 2),
        };

        var boxTop = ToCorelY(pageHeightMm, text.YMm);
        var boxBottom = ToCorelY(pageHeightMm, text.YMm + text.HeightMm);
        shape.CenterY = text.VerticalAlignment switch
        {
            ElementVerticalAlignment.Top => boxTop - (shapeHeight / 2),
            ElementVerticalAlignment.Bottom => boxBottom + (shapeHeight / 2),
            _ => (boxTop + boxBottom) / 2,
        };

        if (text.LetterSpacing != 0)
        {
            shape.Text.Story.CharSpacing = text.LetterSpacing;
        }

        return shape;
    }

    private static void ApplyStyle(dynamic shape, DesignElement element)
    {
        if (element.Fill is not null)
        {
            var (red, green, blue) = ParseRgb(element.Fill.ColorHex);
            shape.Fill.UniformColor.RGBAssign(red, green, blue);
        }
        else if (element is RectangleElement or EllipseElement)
        {
            shape.Fill.ApplyNoFill();
        }

        if (element.Stroke is not null)
        {
            var (red, green, blue) = ParseRgb(element.Stroke.ColorHex);
            shape.Outline.Color.RGBAssign(red, green, blue);
            shape.Outline.Width = element.Stroke.WidthMm;
        }
        else if (element is RectangleElement or EllipseElement or TextElement)
        {
            shape.Outline.SetNoOutline();
        }

        if (element.Opacity < 1)
        {
            shape.Transparency.ApplyUniformTransparency((int)Math.Round((1 - element.Opacity) * 100));
        }
    }

    private static (int Red, int Green, int Blue) ParseRgb(string colorHex) =>
        (
            int.Parse(colorHex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(colorHex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(colorHex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        );

    private static double ToCorelY(double pageHeightMm, double topDownYmm) => pageHeightMm - topDownYmm;

    private static string PrepareOutputPath(string path, string requiredExtension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), requiredExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Output path must use the '{requiredExtension}' extension.", nameof(path));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return fullPath;
    }

    private static string ReadVersion(dynamic application)
    {
        try
        {
            return Convert.ToString(application.Version, CultureInfo.InvariantCulture) ?? "unknown";
        }
        catch
        {
            try
            {
                return Convert.ToString(application.VersionMajor, CultureInfo.InvariantCulture) ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }
    }

    private dynamic EnsureDocument()
    {
        EnsureConnected();
        return _document ?? throw new InvalidOperationException("No document is active. Render a design before saving or exporting.");
    }

    private void EnsureConnected()
    {
        if (_application is null)
        {
            throw new InvalidOperationException("CorelDRAW is not connected. Call Connect first.");
        }
    }

    private void CloseDocumentCore()
    {
        if (_document is null)
        {
            return;
        }

        try
        {
            dynamic document = _document;
            document.Close();
        }
        finally
        {
            ReleaseComObject(_document);
            _document = null;
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private static object? TryGetRunningInstance(Guid classId)
    {
        var result = GetActiveObject(ref classId, IntPtr.Zero, out var instance);
        return result >= 0 ? instance : null;
    }

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(
        ref Guid classId,
        IntPtr reserved,
        [MarshalAs(UnmanagedType.Interface)] out object? instance);

    private void WriteLog(string message) => _log?.Invoke(message);
}

