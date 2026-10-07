using Cinema.Application;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Seats.Commands.LockSeat;
using Cinema.Application.Seats.Commands.UnlockSeat;
using Cinema.Booking.Application;
using Cinema.Booking.Infrastructure;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinema.BookingTests;

public class BookingRegistrationTests
{
    [Fact]
    public void Booking_Registers_One_Handler_Validator_And_Locking_Service_Per_Contract()
    {
        ServiceCollection services = new();
        services.AddApplication();
        services.AddBookingApplication();
        services.AddBookingInfrastructure();

        services.Count(x => x.ServiceType == typeof(IRequestHandler<LockSeatCommand,
            Cinema.Domain.Shared.Result<Cinema.Application.Seats.Dtos.LockSeatResponse>>)).Should().Be(1);
        services.Count(x => x.ServiceType == typeof(IRequestHandler<UnlockSeatCommand,
            Cinema.Domain.Shared.Result<Cinema.Application.Seats.Dtos.UnlockSeatResponse>>)).Should().Be(1);
        services.Count(x => x.ServiceType == typeof(IValidator<LockSeatCommand>)).Should().Be(1);
        services.Count(x => x.ServiceType == typeof(IValidator<UnlockSeatCommand>)).Should().Be(1);
        services.Count(x => x.ServiceType == typeof(ISeatLockingService)).Should().Be(1);
    }
}
