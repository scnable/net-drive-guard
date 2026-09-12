using System.Runtime.InteropServices;
using System.Xml.Linq;
using NetDriveGuard.Core.Startup;

namespace NetDriveGuard.Windows;

public sealed class TaskSchedulerStartupManager : IStartupManager
{
    internal const string ManagedTaskName = "NetDriveGuard";
    internal const string ManagedMarker = "NetDriveGuard.ManagedStartup.v1";
    private readonly IScheduledTaskBackend _backend;

    public TaskSchedulerStartupManager()
        : this(new ComScheduledTaskBackend())
    {
    }

    internal TaskSchedulerStartupManager(IScheduledTaskBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public async Task<StartupRegistrationState> QueryAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        ValidateExecutablePath(executablePath);
        var query = await _backend.QueryAsync(ManagedTaskName, cancellationToken);
        return query.Status switch
        {
            ScheduledTaskQueryStatus.NotFound =>
                new StartupRegistrationState(
                    StartupRegistrationStatus.NotRegistered,
                    "startup.not-registered"),
            ScheduledTaskQueryStatus.Error =>
                new StartupRegistrationState(
                    StartupRegistrationStatus.Error,
                    "startup.query-failed"),
            ScheduledTaskQueryStatus.Found when IsManagedTask(query.Xml, executablePath) =>
                new StartupRegistrationState(
                    StartupRegistrationStatus.Registered,
                    "startup.registered"),
            _ => new StartupRegistrationState(
                StartupRegistrationStatus.Conflict,
                "startup.task-conflict"),
        };
    }

    public async Task<StartupOperationResult> EnableAsync(
        string executablePath,
        string userSid,
        CancellationToken cancellationToken = default)
    {
        ValidateExecutablePath(executablePath);
        ValidateUserSid(userSid);
        var state = await QueryAsync(executablePath, cancellationToken);
        if (state.Status == StartupRegistrationStatus.Conflict)
        {
            return StartupOperationResult.Failed(state.Code);
        }

        if (state.Status == StartupRegistrationStatus.Error)
        {
            return StartupOperationResult.Failed(state.Code);
        }

        var xml = StartupTaskXml.Create(executablePath, userSid);
        var result = await _backend.RegisterAsync(
            ManagedTaskName,
            xml,
            cancellationToken);
        return result.Success
            ? StartupOperationResult.Succeeded("startup.enabled")
            : StartupOperationResult.Failed("startup.enable-failed");
    }

    public async Task<StartupOperationResult> DisableAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        var state = await QueryAsync(executablePath, cancellationToken);
        if (state.Status == StartupRegistrationStatus.NotRegistered)
        {
            return StartupOperationResult.Succeeded("startup.already-disabled");
        }

        if (state.Status != StartupRegistrationStatus.Registered)
        {
            return StartupOperationResult.Failed(state.Code);
        }

        var result = await _backend.DeleteAsync(ManagedTaskName, cancellationToken);
        return result.Success
            ? StartupOperationResult.Succeeded("startup.disabled")
            : StartupOperationResult.Failed("startup.disable-failed");
    }

    private static bool IsManagedTask(string? xml, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return false;
        }

        try
        {
            var document = XDocument.Parse(xml, LoadOptions.None);
            var description = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Description")
                ?.Value;
            var command = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Command")
                ?.Value;
            return string.Equals(description, ManagedMarker, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(command)
                && string.Equals(
                    Path.GetFullPath(command),
                    Path.GetFullPath(executablePath),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is System.Xml.XmlException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static void ValidateExecutablePath(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath)
            || !string.Equals(
                Path.GetExtension(executablePath),
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("启动程序必须是绝对 EXE 路径。", nameof(executablePath));
        }
    }

    private static void ValidateUserSid(string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        if (userSid.Length > 184 || !userSid.StartsWith("S-1-", StringComparison.Ordinal))
        {
            throw new ArgumentException("当前用户 SID 格式无效。", nameof(userSid));
        }
    }
}

internal static class StartupTaskXml
{
    public static string Create(string executablePath, string userSid)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var document = new XDocument(
            new XElement(
                ns + "Task",
                new XAttribute("version", "1.4"),
                new XElement(
                    ns + "RegistrationInfo",
                    new XElement(ns + "Description", TaskSchedulerStartupManager.ManagedMarker)),
                new XElement(
                    ns + "Triggers",
                    new XElement(
                        ns + "LogonTrigger",
                        new XElement(ns + "Enabled", true),
                        new XElement(ns + "UserId", userSid),
                        new XElement(ns + "Delay", "PT5S"))),
                new XElement(
                    ns + "Principals",
                    new XElement(
                        ns + "Principal",
                        new XAttribute("id", "CurrentUser"),
                        new XElement(ns + "UserId", userSid),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", "LeastPrivilege"))),
                new XElement(
                    ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", false),
                    new XElement(ns + "StopIfGoingOnBatteries", false),
                    new XElement(ns + "AllowHardTerminate", true),
                    new XElement(ns + "StartWhenAvailable", true),
                    new XElement(ns + "RunOnlyIfNetworkAvailable", false),
                    new XElement(ns + "AllowStartOnDemand", true),
                    new XElement(ns + "Enabled", true),
                    new XElement(ns + "Hidden", false),
                    new XElement(ns + "RunOnlyIfIdle", false),
                    new XElement(ns + "WakeToRun", false),
                    new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                    new XElement(ns + "Priority", 7)),
                new XElement(
                    ns + "Actions",
                    new XAttribute("Context", "CurrentUser"),
                    new XElement(
                        ns + "Exec",
                        new XElement(ns + "Command", Path.GetFullPath(executablePath)),
                        new XElement(
                            ns + "WorkingDirectory",
                            Path.GetDirectoryName(Path.GetFullPath(executablePath)))))));
        return document.ToString(SaveOptions.DisableFormatting);
    }
}

internal enum ScheduledTaskQueryStatus
{
    NotFound,
    Found,
    Error,
}

internal sealed record ScheduledTaskQueryResult(
    ScheduledTaskQueryStatus Status,
    string? Xml = null);

internal sealed record ScheduledTaskBackendResult(bool Success);

internal interface IScheduledTaskBackend
{
    Task<ScheduledTaskQueryResult> QueryAsync(
        string taskName,
        CancellationToken cancellationToken);

    Task<ScheduledTaskBackendResult> RegisterAsync(
        string taskName,
        string xml,
        CancellationToken cancellationToken);

    Task<ScheduledTaskBackendResult> DeleteAsync(
        string taskName,
        CancellationToken cancellationToken);
}

internal sealed class ComScheduledTaskBackend : IScheduledTaskBackend
{
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const uint ErrorFileNotFound = 0x80070002;
    private const uint TaskNotFound = 0x8004130F;

    public Task<ScheduledTaskQueryResult> QueryAsync(
        string taskName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        object? service = null;
        object? folder = null;
        object? task = null;
        try
        {
            service = CreateService();
            dynamic dynamicService = service;
            dynamicService.Connect();
            folder = dynamicService.GetFolder("\\");
            dynamic dynamicFolder = folder;
            task = dynamicFolder.GetTask(taskName);
            dynamic dynamicTask = task;
            return Task.FromResult(
                new ScheduledTaskQueryResult(
                    ScheduledTaskQueryStatus.Found,
                    (string)dynamicTask.Xml));
        }
        // .NET 会把任务不存在的 HRESULT 转成 FileNotFoundException，不能只捕获 COMException。
        // 只接受明确的“不存在”错误码，权限不足等错误仍按查询失败处理。
        catch (Exception exception) when (
            unchecked((uint)exception.HResult) is ErrorFileNotFound or TaskNotFound)
        {
            return Task.FromResult(
                new ScheduledTaskQueryResult(ScheduledTaskQueryStatus.NotFound));
        }
        catch (COMException)
        {
            return Task.FromResult(
                new ScheduledTaskQueryResult(ScheduledTaskQueryStatus.Error));
        }
        catch (Exception)
        {
            return Task.FromResult(
                new ScheduledTaskQueryResult(ScheduledTaskQueryStatus.Error));
        }
        finally
        {
            ReleaseComObject(task);
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }
    }

    public Task<ScheduledTaskBackendResult> RegisterAsync(
        string taskName,
        string xml,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        object? service = null;
        object? folder = null;
        object? task = null;
        try
        {
            service = CreateService();
            dynamic dynamicService = service;
            dynamicService.Connect();
            folder = dynamicService.GetFolder("\\");
            dynamic dynamicFolder = folder;
            task = dynamicFolder.RegisterTask(
                taskName,
                xml,
                TaskCreateOrUpdate,
                null,
                null,
                TaskLogonInteractiveToken,
                null);
            return Task.FromResult(new ScheduledTaskBackendResult(true));
        }
        catch (COMException)
        {
            return Task.FromResult(new ScheduledTaskBackendResult(false));
        }
        catch (Exception)
        {
            return Task.FromResult(new ScheduledTaskBackendResult(false));
        }
        finally
        {
            ReleaseComObject(task);
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }
    }

    public Task<ScheduledTaskBackendResult> DeleteAsync(
        string taskName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        object? service = null;
        object? folder = null;
        try
        {
            service = CreateService();
            dynamic dynamicService = service;
            dynamicService.Connect();
            folder = dynamicService.GetFolder("\\");
            dynamic dynamicFolder = folder;
            dynamicFolder.DeleteTask(taskName, 0);
            return Task.FromResult(new ScheduledTaskBackendResult(true));
        }
        catch (COMException)
        {
            return Task.FromResult(new ScheduledTaskBackendResult(false));
        }
        catch (Exception)
        {
            return Task.FromResult(new ScheduledTaskBackendResult(false));
        }
        finally
        {
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }
    }

    private static object CreateService()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)
            ?? throw new PlatformNotSupportedException("当前系统不支持任务计划程序。");
        return Activator.CreateInstance(type)
            ?? throw new PlatformNotSupportedException("无法创建任务计划程序连接。");
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
