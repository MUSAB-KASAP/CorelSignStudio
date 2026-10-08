namespace CorelSignStudio.Corel;

public sealed class CorelAutomationException : Exception
{
    public CorelAutomationException(string operation, Exception innerException)
        : base($"CorelDRAW operation '{operation}' failed (HRESULT 0x{innerException.HResult:X8}): {innerException.Message}", innerException)
    {
        Operation = operation;
        HResultCode = innerException.HResult;
    }

    public string Operation { get; }

    public int HResultCode { get; }
}

