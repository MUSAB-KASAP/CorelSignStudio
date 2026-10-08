using CorelSignStudio.Domain;

namespace CorelSignStudio.Corel;

public interface ICorelAutomationService : IAsyncDisposable
{
    Task<CorelConnectionInfo> ConnectAsync(bool visible = false, CancellationToken cancellationToken = default);

    Task RenderDesignAsync(DesignSpec design, CancellationToken cancellationToken = default);

    Task<CorelSaveResult> SaveCdrAsync(string path, CancellationToken cancellationToken = default);

    Task ExportPdfAsync(string path, CancellationToken cancellationToken = default);

    Task<CorelDocumentInfo> OpenCdrAsync(string path, CancellationToken cancellationToken = default);

    Task CloseAsync(CancellationToken cancellationToken = default);
}

public sealed record CorelConnectionInfo(
    string ProgId,
    string Version,
    int StaManagedThreadId,
    ApartmentState ApartmentState);

public sealed record CorelSaveResult(
    string Path,
    string RuntimeSignature,
    string InvocationShape);

public sealed record CorelDocumentInfo(string Path, double WidthMm, double HeightMm);

