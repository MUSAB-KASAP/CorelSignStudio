using System.Collections.ObjectModel;
using System.IO;
using CorelSignStudio.Corel;
using CorelSignStudio.Domain;
using CorelSignStudio.Storage;
using CorelSignStudio.Templates;

namespace CorelSignStudio.App;

public sealed record SignCategoryOption(SignCategory Value, string DisplayName);

public sealed class MainViewModel : ObservableObject
{
    private readonly TemplateCatalog _templateCatalog;
    private readonly IDesignAssetCatalog _assetCatalog;
    private readonly OutputNameGenerator _outputNameGenerator;
    private readonly ICorelAutomationService _corel;
    private readonly IDesktopShellService _shell;
    private readonly FileLogWriter _fileLog;

    private SignCategoryOption _selectedCategory;
    private ISignTemplate? _selectedTemplate;
    private DesignAssetMetadata? _selectedPictogram;
    private double _widthMm = 500;
    private double _heightMm = 700;
    private string _text1 = "BU ALANA";
    private string _text2 = "GİRMEK";
    private string _text3 = "YASAKTIR";
    private string _outputFolder;
    private bool _saveCdr = true;
    private bool _exportPdf = true;
    private bool _isBusy;
    private string _statusMessage = "Hazır";
    private string? _errorMessage;
    private string? _lastCdrPath;
    private DesignSpec? _previewDesign;

    public MainViewModel(
        TemplateCatalog templateCatalog,
        IDesignAssetCatalog assetCatalog,
        OutputNameGenerator outputNameGenerator,
        ICorelAutomationService corel,
        IDesktopShellService shell,
        FileLogWriter fileLog,
        string defaultOutputFolder)
    {
        _templateCatalog = templateCatalog;
        _assetCatalog = assetCatalog;
        _outputNameGenerator = outputNameGenerator;
        _corel = corel;
        _shell = shell;
        _fileLog = fileLog;
        _outputFolder = defaultOutputFolder;

        Categories =
        [
            new(SignCategory.Prohibition, "Yasak"),
            new(SignCategory.Warning, "Uyarı"),
            new(SignCategory.Mandatory, "Zorunluluk"),
            new(SignCategory.Information, "Bilgi"),
        ];
        _selectedCategory = Categories[0];
        Pictograms = _assetCatalog.GetAssets();
        _selectedPictogram = Pictograms.FirstOrDefault();

        CreateCommand = new AsyncRelayCommand(CreateInCorelAsync, () => !IsBusy && SelectedTemplate is not null);
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => !IsBusy);
        OpenOutputCommand = new RelayCommand(OpenOutputFolder);
        OpenCdrCommand = new RelayCommand(OpenLastCdr, () => LastCdrPath is not null);

        RefreshTemplates();
        AddLog("Uygulama hazır. Tasarım önizlemesi DesignSpec üzerinden oluşturuldu.");
    }

    public IReadOnlyList<SignCategoryOption> Categories { get; }
    public ObservableCollection<ISignTemplate> AvailableTemplates { get; } = [];
    public IReadOnlyList<DesignAssetMetadata> Pictograms { get; }
    public ObservableCollection<string> Logs { get; } = [];

    public AsyncRelayCommand CreateCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public RelayCommand OpenOutputCommand { get; }
    public RelayCommand OpenCdrCommand { get; }

    public SignCategoryOption SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                RefreshTemplates();
            }
        }
    }

    public ISignTemplate? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (SetProperty(ref _selectedTemplate, value))
            {
                if (value is not null)
                {
                    WidthMm = value.DefaultWidthMm;
                    HeightMm = value.DefaultHeightMm;
                    SelectedPictogram = Pictograms.FirstOrDefault(asset =>
                        string.Equals(asset.Id, value.DefaultPictogramAssetId, StringComparison.OrdinalIgnoreCase));
                }

                RefreshPreview();
                CreateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public DesignAssetMetadata? SelectedPictogram
    {
        get => _selectedPictogram;
        set
        {
            if (SetProperty(ref _selectedPictogram, value))
            {
                RefreshPreview();
            }
        }
    }

    public double WidthMm { get => _widthMm; set { if (SetProperty(ref _widthMm, value)) RefreshPreview(); } }
    public double HeightMm { get => _heightMm; set { if (SetProperty(ref _heightMm, value)) RefreshPreview(); } }
    public string Text1 { get => _text1; set { if (SetProperty(ref _text1, value)) RefreshPreview(); } }
    public string Text2 { get => _text2; set { if (SetProperty(ref _text2, value)) RefreshPreview(); } }
    public string Text3 { get => _text3; set { if (SetProperty(ref _text3, value)) RefreshPreview(); } }
    public string OutputFolder { get => _outputFolder; set => SetProperty(ref _outputFolder, value); }
    public bool SaveCdr { get => _saveCdr; set => SetProperty(ref _saveCdr, value); }
    public bool ExportPdf { get => _exportPdf; set => SetProperty(ref _exportPdf, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                CreateCommand.RaiseCanExecuteChanged();
                BrowseOutputCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string? ErrorMessage { get => _errorMessage; private set { SetProperty(ref _errorMessage, value); OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string? LastCdrPath
    {
        get => _lastCdrPath;
        private set
        {
            if (SetProperty(ref _lastCdrPath, value))
            {
                OpenCdrCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public DesignSpec? PreviewDesign { get => _previewDesign; private set => SetProperty(ref _previewDesign, value); }
    public IDesignAssetResolver AssetResolver => _assetCatalog;
    public string LogPath => _fileLog.LogPath;

    private void RefreshTemplates()
    {
        AvailableTemplates.Clear();
        foreach (var template in _templateCatalog.GetByCategory(SelectedCategory.Value))
        {
            AvailableTemplates.Add(template);
        }

        SelectedTemplate = AvailableTemplates.FirstOrDefault();
        if (SelectedTemplate is null)
        {
            PreviewDesign = null;
            StatusMessage = "Bu tabela türü için henüz şablon bulunmuyor.";
        }
        else
        {
            StatusMessage = "Hazır";
        }
    }

    private void RefreshPreview()
    {
        try
        {
            PreviewDesign = CreateDesign();
            ErrorMessage = null;
        }
        catch (Exception exception)
        {
            PreviewDesign = null;
            ErrorMessage = FriendlyMessage(exception);
        }
    }

    private DesignSpec CreateDesign()
    {
        var template = SelectedTemplate ?? throw new InvalidOperationException("Lütfen bir şablon seçin.");
        var pictogram = SelectedPictogram ?? throw new InvalidOperationException("Lütfen bir piktogram seçin.");
        return template.CreateDesign(new SignTemplateParameters(
            WidthMm,
            HeightMm,
            Text1,
            Text2,
            Text3,
            pictogram.Id));
    }

    private async Task CreateInCorelAsync()
    {
        if (!SaveCdr && !ExportPdf)
        {
            ErrorMessage = "En az bir çıktı biçimi seçin: CDR veya PDF.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var design = CreateDesign();
            var paths = _outputNameGenerator.CreateAvailablePaths(
                OutputFolder,
                [Text1, Text2, Text3],
                WidthMm,
                HeightMm,
                SaveCdr,
                ExportPdf);

            AddLog("CorelDRAW 2026 bağlantısı kuruluyor…");
            StatusMessage = "CorelDRAW'a bağlanılıyor";
            var connection = await _corel.ConnectAsync(visible: true);
            AddLog($"CorelDRAW {connection.Version} bağlantısı hazır (STA iş parçacığı {connection.StaManagedThreadId}).");

            StatusMessage = "Tasarım oluşturuluyor";
            AddLog($"{design.WidthMm:0.##} × {design.HeightMm:0.##} mm tasarım işleniyor…");
            await _corel.RenderDesignAsync(design);

            if (paths.CdrPath is not null)
            {
                StatusMessage = "CDR kaydediliyor";
                var save = await _corel.SaveCdrAsync(paths.CdrPath);
                LastCdrPath = save.Path;
                AddLog($"CDR kaydedildi: {save.Path}");
            }

            if (paths.PdfPath is not null)
            {
                StatusMessage = "PDF dışa aktarılıyor";
                await _corel.ExportPdfAsync(paths.PdfPath);
                AddLog($"PDF kaydedildi: {paths.PdfPath}");
            }

            StatusMessage = "Tamamlandı";
            AddLog("İşlem başarıyla tamamlandı. Tasarım CorelDRAW'da açık bırakıldı.");
        }
        catch (Exception exception)
        {
            StatusMessage = "İşlem tamamlanamadı";
            ErrorMessage = FriendlyMessage(exception);
            AddLog($"Hata: {ErrorMessage}");
            _fileLog.Write("CorelDRAW üretim işlemi başarısız oldu.", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BrowseOutput()
    {
        var selected = _shell.BrowseForFolder(OutputFolder);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            OutputFolder = selected;
        }
    }

    private void OpenOutputFolder()
    {
        try
        {
            Directory.CreateDirectory(OutputFolder);
            _shell.OpenFolder(OutputFolder);
        }
        catch (Exception exception)
        {
            ErrorMessage = FriendlyMessage(exception);
            _fileLog.Write("Çıktı klasörü açılamadı.", exception);
        }
    }

    private void OpenLastCdr()
    {
        try
        {
            if (LastCdrPath is not null)
            {
                _shell.OpenFile(LastCdrPath);
            }
        }
        catch (Exception exception)
        {
            ErrorMessage = FriendlyMessage(exception);
            _fileLog.Write("CDR dosyası açılamadı.", exception);
        }
    }

    private void AddLog(string message)
    {
        var entry = $"{DateTime.Now:HH:mm:ss}  {message}";
        Logs.Add(entry);
        _fileLog.Write(message);
    }

    private static string FriendlyMessage(Exception exception) => exception switch
    {
        ArgumentException => exception.Message,
        FileNotFoundException => "Gerekli dosya bulunamadı. Teknik ayrıntılar günlük dosyasına yazıldı.",
        UnauthorizedAccessException => "Seçilen klasöre yazma izni yok. Başka bir çıktı klasörü seçin.",
        _ => "İşlem sırasında beklenmeyen bir sorun oluştu. Teknik ayrıntılar günlük dosyasına yazıldı.",
    };
}
