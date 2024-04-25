using System.Text.Json.Nodes;
using System.Web;
using MaaToolKit.Extensions;
using MaaToolKit.Extensions.Enums;
using MBA.Core.Data;
using MBA.Core.Enums;
using MBA.Core.Managers;
using RestSharp;

namespace MBA.Core;

public class Main
{
    private static Serilog.ILogger Log => LogManager.Logger;

    public static Main Current { get; } = new();

    // TODO: 去除静态
    private static Config Config { get; } = ConfigManager.Config;

    public void Start()
    {
        var location = $"{nameof(Main)}.{nameof(Start)}";

        TaskManager.RunTask(() =>
        {
            // remark: lock config
            ConfigManager.LogConfig();

            // TODO: GetMaa 抛出的错误应该是可选择的
            using var maa = GetMaa();
            var tasks = GetTasks();
            if (!maa.Instance.Initialized)
                Log.Error("Failed to init Maa instance, a connection error or resource file corruption occurred, please refer to the log.");

            var runningResult = TryRunTasks(maa, tasks);
            var failedTasks = runningResult.Where(pair => !pair.Value).Select(pair => pair.Key).ToList();
            if (!failedTasks.Any())
                Log.Information("Congratulations! All tasks have been successful!");
            else
            {
                Log.Warning("Unfortunately, some tasks have failed...");
                var failedTasksStr = string.Join(", ", failedTasks);
                var report = $"MBA任务执行完成，但是以下任务失败了：{failedTasksStr}。";

                if (Config.Webhook.Enable)
                {
                    Log.Information("Executing webhook...");

                    var url = Config.Webhook.Url.Replace("#{report}", HttpUtility.UrlEncode(report));
                    var client = new RestClient(url);

                    _ = Enum.TryParse(Config.Webhook.Method, out Method method);
                    var request = new RestRequest
                    {
                        Method = method,
                    };
                    request.AddBody(Config.Webhook.Body.Replace("#{report}", report));
                    request.AddHeaders(Config.Webhook.Headers);

                    var reponse = client.Execute(request);
                }
            }

        },
        location);
    }

    private MaaObject GetMaa()
    {
        var maa = new MaaObject(
                Config.Core.Adb,
                Config.Core.AdbAddress,
                Config.Core.ControlType,
                ConfigManager.AdbConfig,
                GlobalInfo.BaseResourceFullPath,
                $"{GlobalInfo.ResourceFullPath}/{Config.Game.Language}");

        maa.Controller.SetOption(
            ControllerOption.ScreenshotTargetShortSide,
            GlobalInfo.ScreenshotHeight);
        maa.Controller.SetOption(
            ControllerOption.DefaultAppPackageEntry,
            Config.Game.LanguageServer.GetPackageEntry());

        return maa;
    }

    private List<TaskType> GetTasks()
    {
        var configTasks = new List<TaskType>(Config.Tasks);
        configTasks.Replace(TaskType.Daily, Config.Game.Server);
        configTasks.RemoveAll(Config.TasksExcept.Contains);
        return configTasks;
    }

    private Dictionary<TaskType, bool> TryRunTasks(MaaObject maa, List<TaskType> tasks)
    {
        Log.Information("Task List: {list}.", tasks);
        tasks.Replace(Config.Game.Server);
        Log.Debug("Entry Task List: {list}.", tasks);

        var runningResult = new Dictionary<TaskType, bool>();
        var diffTasks = new DiffTasks(Config);

        foreach (TaskType task in tasks)
        {
            if (task.Enabled(diffTasks, out var diffTask))
            {
                Log.Information("{task} Started.", task);
            }
            else
            {
                Log.Debug("{task} Skipped.", task);
                continue;
            }

            var status = maa.Instance
                            .AppendTask(task.ToString(), diffTask)
                            .Wait();

            // TODO: MaaJob 和 MaaJobStatus 包含 任务名 及其参数

            if (status == MaaJobStatus.Success)
            {
                runningResult[task] = true;
                Log.Information("{task} Completed. Result: {status}", task, status);
            }
            else
            {
                runningResult[task] = false;
                Log.Error("{task} Completed. Result: {status}", task, status);
            }
        }

        return runningResult;
    }
}
