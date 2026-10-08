using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace CorelSignStudio.Corel;

internal static class ComDispatchInspector
{
    public static string GetMethodSignature(object comObject, string methodName)
    {
        ArgumentNullException.ThrowIfNull(comObject);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);

        if (comObject is not IDispatch dispatch)
        {
            throw new InvalidOperationException("The Corel object does not expose IDispatch type information.");
        }

        Marshal.ThrowExceptionForHR(dispatch.GetTypeInfo(0, 0, out var typeInfo));
        try
        {
            typeInfo.GetTypeAttr(out var typeAttributePointer);
            try
            {
                var typeAttribute = Marshal.PtrToStructure<TYPEATTR>(typeAttributePointer);
                for (var index = 0; index < typeAttribute.cFuncs; index++)
                {
                    typeInfo.GetFuncDesc(index, out var functionPointer);
                    try
                    {
                        var function = Marshal.PtrToStructure<FUNCDESC>(functionPointer);
                        var names = new string[function.cParams + 1];
                        typeInfo.GetNames(function.memid, names, names.Length, out var nameCount);
                        if (nameCount == 0 || !string.Equals(names[0], methodName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var parameters = names.Skip(1).Take(nameCount - 1).ToArray();
                        var optionalStart = function.cParams - function.cParamsOpt;
                        var renderedParameters = parameters
                            .Select((name, parameterIndex) => parameterIndex >= optionalStart ? $"[{name}]" : name);

                        return $"{names[0]}({string.Join(", ", renderedParameters)}) " +
                               $"params={function.cParams}, optional={function.cParamsOpt}, invoke={function.invkind}";
                    }
                    finally
                    {
                        typeInfo.ReleaseFuncDesc(functionPointer);
                    }
                }
            }
            finally
            {
                typeInfo.ReleaseTypeAttr(typeAttributePointer);
            }
        }
        finally
        {
            if (Marshal.IsComObject(typeInfo))
            {
                Marshal.FinalReleaseComObject(typeInfo);
            }
        }

        throw new MissingMethodException($"Method '{methodName}' was not present in the runtime COM type information.");
    }

    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDispatch
    {
        [PreserveSig]
        int GetTypeInfoCount(out uint count);

        [PreserveSig]
        int GetTypeInfo(uint typeInfo, uint localeId, [MarshalAs(UnmanagedType.Interface)] out ITypeInfo typeInformation);

        [PreserveSig]
        int GetIdsOfNames(
            ref Guid interfaceId,
            IntPtr names,
            uint nameCount,
            uint localeId,
            IntPtr dispatchIds);

        [PreserveSig]
        int Invoke(
            int dispatchId,
            ref Guid interfaceId,
            uint localeId,
            ushort flags,
            IntPtr parameters,
            IntPtr result,
            IntPtr exceptionInfo,
            IntPtr argumentError);
    }
}

