using System;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DivisiBillWs;

public class CleanupFunction
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
    public async Task Run([TimerTrigger("0 0 9 * * Wed")] TimerInfo myTimer) // Run at 9 am every Sunday
    {
        _logger.LogInformation("CleanupFunction executed at: {executionTime}", DateTime.Now);
        
        if (myTimer.ScheduleStatus is not null)
        {
            _logger.LogInformation("In CleanupFunction, next timer schedule at: {nextSchedule}", myTimer.ScheduleStatus.Next);
        }
        await venueListStorage.CleanupAllUsersAsync();
        await personListStorage.CleanupAllUsersAsync();
    }
}