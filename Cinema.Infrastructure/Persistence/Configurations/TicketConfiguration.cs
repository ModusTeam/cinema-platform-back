using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinema.Infrastructure.Persistence.Configurations;

public class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasConversion(x => x.Value, x => new EntityId<Ticket>(x));

        builder.Property(x => x.PriceSnapshot)
            .IsRequired()
            .HasPrecision(18, 2);

        builder.Property(x => x.TicketStatus)
            .HasConversion<int>();

        builder.Property(x => x.OrderId)
            .HasConversion(x => x.Value, x => new EntityId<Order>(x));

        builder.HasOne(x => x.Order)
            .WithMany(x => x.Tickets)
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.SessionId)
            .HasConversion(x => x.Value, x => new EntityId<Session>(x));

        builder.HasOne(x => x.Session)
            .WithMany(x => x.Tickets)
            .HasForeignKey(x => x.SessionId)    
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.SeatId)
            .HasConversion(x => x.Value, x => new EntityId<Seat>(x));

        builder.HasOne(x => x.Seat)
            .WithMany()
            .HasForeignKey(x => x.SeatId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasQueryFilter(t => t.Seat!.Hall!.IsActive);
        
        builder.HasIndex(t => new { t.SessionId, t.SeatId })
            .IsUnique()
            .HasFilter($"ticket_status IN ({(int)TicketStatus.Valid}, {(int)TicketStatus.Used})");
        builder.HasIndex(t => new { t.SessionId, t.SeatId })
            .IsUnique()
            .HasFilter("\"TicketStatus\" IN (1, 2)");
    }
}