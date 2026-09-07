using Microsoft.Extensions.Logging;

namespace DivisiBillWs;

public partial class CleanupFunction
{
    private readonly ILogger _logger;
    internal readonly DataStore<VenueListStorage> venueListStorage;
    internal readonly DataStore<PersonListStorage> personListStorage;

    public CleanupFunction(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<CleanupFunction>();
        venueListStorage = new(_logger, null);
        personListStorage = new(_logger, null);
    }

    [Function("CleanupFunction")]
    public async Task Run([TimerTrigger("0 0 9 * * Wed")] TimerInfo myTimer) // Run at 9 am every Wednesday
    {
        LogCleanupFunctionExecuted(DateTime.Now);

        if (myTimer.ScheduleStatus is not null)
        {
            LogCleanupFunctionNextSchedule(myTimer.ScheduleStatus.Next);
        }
        await venueListStorage.CleanupAllUsersAsync();
        await personListStorage.CleanupAllUsersAsync();
    }

    [LoggerMessage(LogLevel.Information, "CleanupFunction executed at: {executionTime}")]
    private partial void LogCleanupFunctionExecuted(DateTime executionTime);

    [LoggerMessage(LogLevel.Information, "In CleanupFunction, next timer schedule at: {nextSchedule}")]
    private partial void LogCleanupFunctionNextSchedule(DateTime nextSchedule);
}