using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RaceToFinish.Services;

/// <summary>
/// Runs once at server start: asks File Transformation to add our script to index.html.
/// </summary>
public class StartupTask : IScheduledTask
{
    /// <summary>Fixed ID for our index.html transformation, so restarts replace rather than duplicate it.</summary>
    public const string IndexTransformationId = "51a00e15-b516-4982-938e-96aa82c2d354";

    private readonly ILogger<StartupTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="StartupTask"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public StartupTask(ILogger<StartupTask> logger)
    {
        _logger = logger;
    }

    /// <summary>Gets a value indicating whether File Transformation accepted our registration this run.</summary>
    public static bool InjectionRegistered { get; private set; }

    /// <summary>Gets the reason injection failed, if it did.</summary>
    public static string? InjectionError { get; private set; }

    /// <inheritdoc />
    public string Name => "Race to Finish startup";

    /// <inheritdoc />
    public string Key => "RaceToFinishStartup";

    /// <inheritdoc />
    public string Description => "Adds the Race to Finish button and popup to the web client.";

    /// <inheritdoc />
    public string Category => "Race to Finish";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        RegisterIndexTransformation();
        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
    }

    private void RegisterIndexTransformation()
    {
        var assembly = PluginBridge.FindAssembly(".FileTransformation");
        if (assembly is null)
        {
            InjectionError = "File Transformation is not installed. Install it from the catalog, then restart Jellyfin.";
            _logger.LogWarning("[RaceToFinish] {Error}", InjectionError);
            return;
        }

        var payload = new Dictionary<string, string>
        {
            ["id"] = IndexTransformationId,
            ["fileNamePattern"] = "index.html",
            ["callbackAssembly"] = typeof(IndexHtmlTransformer).Assembly.FullName!,
            ["callbackClass"] = typeof(IndexHtmlTransformer).FullName!,
            ["callbackMethod"] = nameof(IndexHtmlTransformer.Transform),
        };

        try
        {
            string? error = PluginBridge.InvokeWithJson(assembly, "Jellyfin.Plugin.FileTransformation.PluginInterface", "RegisterTransformation", payload);
            if (error is null)
            {
                InjectionRegistered = true;
                InjectionError = null;
                _logger.LogInformation("[RaceToFinish] Registered index.html transformation with File Transformation.");
            }
            else
            {
                InjectionError = "File Transformation is installed but did not accept the registration (" + error + "). It may need updating.";
                _logger.LogWarning("[RaceToFinish] {Error}", InjectionError);
            }
        }
        catch (Exception ex)
        {
            InjectionError = "Registering with File Transformation failed: " + ex.GetBaseException().Message;
            _logger.LogError(ex, "[RaceToFinish] Registering with File Transformation failed");
        }
    }
}
