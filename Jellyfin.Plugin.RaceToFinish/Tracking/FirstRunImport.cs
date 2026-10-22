using Jellyfin.Plugin.RaceToFinish.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RaceToFinish.Tracking;

/// <summary>
/// Imports existing history once, the first time the plugin runs, so nobody has to press a button.
/// Waits a little after startup so the library has finished loading. The admin can re-run it any time.
/// </summary>
public sealed class FirstRunImport : BackgroundService
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(45);

    private readonly HistoryDatabase _database;
    private readonly HistoryImporter _importer;
    private readonly ILogger<FirstRunImport> _logger;

    /// <summary>Initializes a new instance of the <see cref="FirstRunImport"/> class.</summary>
    /// <param name="database">History database.</param>
    /// <param name="importer">Importer.</param>
    /// <param name="logger">Logger.</param>
    public FirstRunImport(HistoryDatabase database, HistoryImporter importer, ILogger<FirstRunImport> logger)
    {
        _database = database;
        _importer = importer;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_database.GetMeta("last_import_utc") is not null)
        {
            return;
        }

        try
        {
            await Task.Delay(Delay, stoppingToken).ConfigureAwait(false);
            _logger.LogInformation("[RaceToFinish] First run: importing existing viewing history");
            await _importer.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Server stopping.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RaceToFinish] First-run history import failed");
        }
    }
}
