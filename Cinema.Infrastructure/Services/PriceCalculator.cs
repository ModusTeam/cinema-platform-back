using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Exceptions;

namespace Cinema.Infrastructure.Services;

public class PriceCalculator : IPriceCalculator
{
    public decimal CalculatePrice(
        Pricing pricing, 
        EntityId<SeatType> seatTypeId, 
        DateTime sessionStartTime)
    {
        var relevantItems = pricing.PricingItems?
            .Where(pi => pi.SeatTypeId == seatTypeId)
            .ToList();

        if (relevantItems == null || !relevantItems.Any())
        {
            throw new PriceNotConfiguredException(pricing.Name, seatTypeId.ToString(), sessionStartTime);
        }
        
        var requestTime = sessionStartTime.TimeOfDay;
        var requestDay = sessionStartTime.DayOfWeek;

        var bestMatch = relevantItems
            .Where(item => item.DayOfWeek == requestDay)
            .Where(item => IsTimeApplicable(item, requestTime))
            .OrderByDescending(item => item.StartTime.HasValue && item.EndTime.HasValue) 
            .FirstOrDefault();

        if (bestMatch != null)
        {
            return bestMatch.Price;
        }
        
        throw new PriceNotConfiguredException(pricing.Name, seatTypeId.ToString(), sessionStartTime);
    }
    
    private static bool IsTimeApplicable(PricingItem item, TimeSpan sessionTime)
    {
        if (!item.StartTime.HasValue || !item.EndTime.HasValue)
            return true;

        var start = item.StartTime.Value.TimeOfDay;
        var end = item.EndTime.Value.TimeOfDay;
        
        if (start <= end)
        {
            return sessionTime >= start && sessionTime <= end;
        }
        else
        {
            return sessionTime >= start || sessionTime <= end;
        }
    }
}