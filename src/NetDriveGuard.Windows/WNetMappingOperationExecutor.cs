using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NetDriveGuard.Core.Mapping;
using NetDriveGuard.Core.Security;

namespace NetDriveGuard.Windows;

/// <summary>
/// 只执行由核心规划器签发的精确映射操作计划。
/// </summary>
public sealed class WNetMappingOperationExecutor : IMappingOperationExecutor
{
    private readonly IWNetMappingApi _api;

    public WNetMappingOperationExecutor()
        : this(new WNetMappingApi())
    {
    }

    internal WNetMappingOperationExecutor(IWNetMappingApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public MappingExecutionResult Execute(
        MappingOperationPlan plan,
        NetworkCredentialSecret? credential = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanExecute
            || string.IsNullOrWhiteSpace(plan.DriveLetter)
            || string.IsNullOrWhiteSpace(plan.SharePath))
        {
            return MappingExecutionResult.Failed("mapping.execution-not-authorized");
        }

        // 先非强制断开旧映射，成功后才连接新地址；占用中的文件不会被强制关闭。
        if (plan.Operation == MappingOperation.Replace)
        {
            var disconnectError = _api.Disconnect(plan.DriveLetter, updateProfile: true, force: false);
            if (disconnectError is not (0 or 2250))
            {
                return MappingExecutionResult.Failed(
                    disconnectError is 2401 or 2404 ? "mapping.in-use" : "mapping.disconnect-failed",
                    disconnectError);
            }
        }

        var errorCode = plan.Operation switch
        {
            MappingOperation.Connect or MappingOperation.Replace => _api.Connect(
                plan.DriveLetter,
                plan.SharePath,
                credential?.UserName,
                credential is null ? default : credential.Password,
                credential is null),
            MappingOperation.Disconnect => _api.Disconnect(
                plan.DriveLetter,
                updateProfile: true,
                force: false),
            _ => -1,
        };

        if (errorCode == 0)
        {
            return MappingExecutionResult.Succeeded(
                plan.Operation is MappingOperation.Connect or MappingOperation.Replace
                    ? "mapping.connected"
                    : "mapping.disconnected");
        }

        return MappingExecutionResult.Failed(
            errorCode is 2401 or 2404 ? "mapping.in-use"
                : plan.Operation is MappingOperation.Connect or MappingOperation.Replace
                    ? "mapping.connect-failed"
                    : "mapping.disconnect-failed",
            errorCode);
    }
}

internal interface IWNetMappingApi
{
    int Connect(
        string localName,
        string remoteName,
        string? userName,
        ReadOnlySpan<char> password,
        bool useDefaultCredentials);

    int Disconnect(string localName, bool updateProfile, bool force);
}

internal sealed class WNetMappingApi : IWNetMappingApi
{
    private const uint ResourceTypeDisk = 1;
    private const uint ConnectUpdateProfile = 1;

    public int Connect(
        string localName,
        string remoteName,
        string? userName,
        ReadOnlySpan<char> password,
        bool useDefaultCredentials)
    {
        var resource = new NativeNetworkResource
        {
            Type = ResourceTypeDisk,
            LocalName = localName,
            RemoteName = remoteName,
        };

        if (useDefaultCredentials)
        {
            return WNetAddConnection2(ref resource, IntPtr.Zero, null, ConnectUpdateProfile);
        }

        var nullTerminatedPassword = new char[password.Length + 1];
        password.CopyTo(nullTerminatedPassword);
        var handle = GCHandle.Alloc(nullTerminatedPassword, GCHandleType.Pinned);
        try
        {
            return WNetAddConnection2(
                ref resource,
                handle.AddrOfPinnedObject(),
                userName,
                ConnectUpdateProfile);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(
                MemoryMarshal.AsBytes(nullTerminatedPassword.AsSpan()));
            handle.Free();
        }
    }

    public int Disconnect(string localName, bool updateProfile, bool force)
    {
        return WNetCancelConnection2(
            localName,
            updateProfile ? ConnectUpdateProfile : 0,
            force);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeNetworkResource
    {
        public uint Scope;
        public uint Type;
        public uint DisplayType;
        public uint Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", EntryPoint = "WNetAddConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(
        ref NativeNetworkResource networkResource,
        IntPtr password,
        string? userName,
        uint flags);

    [DllImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(
        string name,
        uint flags,
        [MarshalAs(UnmanagedType.Bool)] bool force);
}
