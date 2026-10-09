using CorelSignStudio.Domain.Inspection;

namespace CorelSignStudio.Corel;

/// <summary>
/// Reads the active CorelDRAW document into an application-neutral <see cref="DocumentSnapshot"/>.
/// Inspection is read-only apart from switching the document's automation unit to millimetres.
/// </summary>
public sealed class CorelDocumentInspector(CorelAutomationService service) : ICorelDocumentInspector
{
    public Task<DocumentSnapshot?> InspectActiveDocumentAsync(CancellationToken cancellationToken = default) =>
        service.RunAsync("InspectDocument", application =>
        {
            var snapshot = Build(application);
            service.Log(snapshot is null
                ? "Inspection: no document is open."
                : $"Inspected '{snapshot.Title}': {snapshot.Pages.Count} page(s), {snapshot.ShapeCount} object(s).");
            return snapshot;
        }, cancellationToken);

    internal static DocumentSnapshot? Build(object applicationObject)
    {
        dynamic application = applicationObject;
        object? documentObject = application.ActiveDocument;
        if (documentObject is null)
        {
            return null;
        }

        dynamic document = documentObject;
        document.Unit = CorelShapes.UnitMillimeter;

        var activePageIndex = CorelShapes.Try(() => (int)document.ActivePage.Index, 1);
        var pages = new List<PageSnapshot>();
        dynamic corelPages = document.Pages;
        int pageCount = corelPages.Count;
        for (var pageIndex = 1; pageIndex <= pageCount; pageIndex++)
        {
            pages.Add((PageSnapshot)BuildPage(application, corelPages[pageIndex], pageIndex));
        }

        var selected = new List<string>();
        try
        {
            dynamic selection = document.SelectionRange;
            int selectionCount = selection.Count;
            for (var index = 1; index <= selectionCount; index++)
            {
                selected.Add((string)CorelShapes.LogicalId(selection[index]));
            }
        }
        catch
        {
            // A selection is optional information.
        }

        var filePath = CorelShapes.Try(() => (string?)document.FullFileName, null);
        return new DocumentSnapshot
        {
            Title = CorelShapes.Try(() => (string?)document.Title, null)
                    ?? CorelShapes.Try(() => (string?)document.Name, null)
                    ?? "Untitled",
            FilePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath,
            ActivePageIndex = activePageIndex,
            Pages = pages,
            SelectedShapeIds = selected,
        };
    }

    private static PageSnapshot BuildPage(dynamic application, dynamic page, int pageIndex)
    {
        PageFrame frame = PageFrame.Of(page);
        var layers = new List<LayerSnapshot>();
        var shapes = new List<ShapeSnapshot>();
        var order = 0;

        dynamic corelLayers = page.Layers;
        int layerCount = corelLayers.Count;
        for (var layerIndex = 1; layerIndex <= layerCount; layerIndex++)
        {
            dynamic layer = corelLayers[layerIndex];

            // Guides, grid and desktop layers hold no artwork.
            if (CorelShapes.Try(() => (bool)layer.IsSpecialLayer, false))
            {
                continue;
            }

            string layerName = CorelShapes.Try(() => (string)layer.Name, $"Layer {layerIndex}")!;
            dynamic layerShapes = layer.Shapes;
            int shapeCount = layerShapes.Count;
            layers.Add(new LayerSnapshot
            {
                Name = layerName,
                Visible = CorelShapes.Try(() => (bool)layer.Visible, true),
                Locked = !CorelShapes.Try(() => (bool)layer.Editable, true),
                Printable = CorelShapes.Try(() => (bool)layer.Printable, true),
                ShapeCount = shapeCount,
            });

            for (var shapeIndex = 1; shapeIndex <= shapeCount; shapeIndex++)
            {
                shapes.Add((ShapeSnapshot)BuildShape(application, layerShapes[shapeIndex], frame, layerName, pageIndex, null, ref order));
            }
        }

        return new PageSnapshot
        {
            Index = pageIndex,
            Name = CorelShapes.Try(() => (string?)page.Name, null),
            WidthMm = Math.Round(frame.Width, 4),
            HeightMm = Math.Round(frame.Height, 4),
            Layers = layers,
            Shapes = shapes,
        };
    }

    private static ShapeSnapshot BuildShape(
        dynamic application,
        dynamic shape,
        PageFrame frame,
        string layerName,
        int pageIndex,
        string? parentGroupId,
        ref int order)
    {
        int nativeId = shape.StaticID;
        int nativeType = shape.Type;
        var id = LogicalShapeId.FromNativeId(nativeId);
        var myOrder = ++order;

        var kind = nativeType switch
        {
            1 => ShapeKind.Rectangle,
            2 => ShapeKind.Ellipse,
            3 => ShapeKind.Curve,
            4 => ShapeKind.Polygon,
            5 => ShapeKind.Bitmap,
            7 => ShapeKind.Group,
            9 => ShapeKind.Guideline,
            23 => ShapeKind.Symbol,
            _ => ShapeKind.Other,
        };

        string? text = null;
        string? font = null;
        double? fontSize = null;
        if (nativeType == CorelShapes.ShapeTypeText)
        {
            dynamic corelText = shape.Text;
            kind = CorelShapes.Try(() => (int)corelText.Type, 0) is 1 or 3 ? ShapeKind.ParagraphText : ShapeKind.ArtisticText;
            text = CorelShapes.Try(() => (string?)corelText.Story.Text, null)?.TrimEnd('\r', '\n');
            font = CorelShapes.Try(() => (string?)corelText.Story.Font, null);
            fontSize = CorelShapes.Try(() => (double?)(float)corelText.Story.Size, null);
        }
        else if (nativeType == CorelShapes.ShapeTypeCustom &&
                 string.Equals(CorelShapes.Try(() => (string?)shape.Custom.TypeID, null), "Table", StringComparison.OrdinalIgnoreCase))
        {
            kind = ShapeKind.Table;
        }

        // Shape.Bitmap.SizeWidth/SizeHeight are the stored pixel dimensions (verified on CorelDRAW 2026).
        BitmapInfo? bitmap = null;
        if (kind == ShapeKind.Bitmap)
        {
            var pixelWidth = CorelShapes.Try(() => (int)shape.Bitmap.SizeWidth, 0);
            var pixelHeight = CorelShapes.Try(() => (int)shape.Bitmap.SizeHeight, 0);
            bitmap = pixelWidth > 0 && pixelHeight > 0 ? new BitmapInfo(pixelWidth, pixelHeight) : null;
        }

        var children = new List<ShapeSnapshot>();
        if (nativeType == CorelShapes.ShapeTypeGroup)
        {
            dynamic nested = shape.Shapes;
            int nestedCount = nested.Count;
            for (var index = 1; index <= nestedCount; index++)
            {
                children.Add((ShapeSnapshot)BuildShape(application, nested[index], frame, layerName, pageIndex, id, ref order));
            }
        }

        return new ShapeSnapshot
        {
            Id = id,
            NativeId = nativeId,
            Type = kind,
            NativeType = CorelShapes.NativeTypeName(nativeType),
            Name = NullIfEmpty(CorelShapes.Try(() => (string?)shape.Name, null)),
            Text = text,
            FontFamily = font,
            FontSizePt = fontSize,
            Bounds = (BoundsMm)frame.BoundsOf(shape),
            RotationDegrees = Math.Round(CorelShapes.Try(() => (double)shape.RotationAngle, 0d), 4),
            Bitmap = bitmap,
            Fill = kind == ShapeKind.Group ? null : (FillInfo?)ReadFill(application, shape),
            Outline = kind == ShapeKind.Group ? null : (OutlineInfo?)ReadOutline(application, shape),
            LayerName = layerName,
            PageIndex = pageIndex,
            ParentGroupId = parentGroupId,
            Children = children,
            Visible = CorelShapes.Try(() => (bool)shape.Visible, true),
            Locked = CorelShapes.Try(() => (bool)shape.Locked, false),
            Order = myOrder,
        };
    }

    private static FillInfo? ReadFill(dynamic application, dynamic shape)
    {
        try
        {
            dynamic fill = shape.Fill;
            int type = fill.Type;
            return type switch
            {
                0 => new FillInfo(FillKind.None),
                1 => new FillInfo(FillKind.Uniform, (string?)CorelShapes.ReadColorHex(application, fill.UniformColor)),
                2 => new FillInfo(FillKind.Fountain),
                8 => new FillInfo(FillKind.Texture),
                9 => new FillInfo(FillKind.Pattern),
                _ => new FillInfo(FillKind.Other),
            };
        }
        catch
        {
            return null;
        }
    }

    private static OutlineInfo? ReadOutline(dynamic application, dynamic shape)
    {
        try
        {
            dynamic outline = shape.Outline;
            if ((int)outline.Type == 0)
            {
                return new OutlineInfo(false);
            }

            return new OutlineInfo(
                true,
                (string?)CorelShapes.ReadColorHex(application, outline.Color),
                Math.Round((double)outline.Width, 4));
        }
        catch
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
