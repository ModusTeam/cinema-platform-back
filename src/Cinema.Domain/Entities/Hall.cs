using Cinema.Domain.Common;
using Cinema.Domain.Enums;
using Cinema.Domain.Exceptions;

namespace Cinema.Domain.Entities;

public class Hall
{
    public EntityId<Hall> Id { get; }
    public string Name { get; private set; }
    public int TotalCapacity { get; private set; }
    public bool IsActive { get; private set; } = true;
    
    private readonly List<Seat> _seats = new();
    public IReadOnlyCollection<Seat> Seats => _seats.AsReadOnly();

    private readonly List<HallTechnology> _technologies = new();
    public IReadOnlyCollection<HallTechnology> Technologies => _technologies.AsReadOnly();
    
    public ICollection<Session> Sessions { get; private set; } = [];

    private Hall(EntityId<Hall> id, string name)
    {
        Id = id;
        Name = name;
        TotalCapacity = 0;
    }
    
    public static Hall Create(EntityId<Hall> id, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Hall name cannot be empty.");
            
        return new Hall(id, name);
    }

    public void ApplyLayout(IEnumerable<Seat> seats)
    {
        if (_seats.Count != 0) 
            throw new DomainException("Cannot apply layout for a hall that already has seats.");

        var seatList = seats.ToList();
        if (seatList.Count == 0)
            throw new DomainException("Cannot apply an empty seat layout.");

        if (seatList.Any(s => s.HallId != this.Id))
             throw new DomainException("One or more seats belong to a different hall.");

        _seats.AddRange(seatList);
        RecalculateCapacity();
    }

    public void UpdateTechnologies(IEnumerable<EntityId<Technology>> newTechnologyIds)
    {
        var newIdsSet = new HashSet<EntityId<Technology>>(newTechnologyIds);
        _technologies.RemoveAll(ht => !newIdsSet.Contains(ht.TechnologyId));
        foreach (var techId in newIdsSet)
        {
            if (!_technologies.Any(ht => ht.TechnologyId == techId))
            {
                _technologies.Add(HallTechnology.New(this.Id, techId));
            }
        }
    }

    public void UpdateDetails(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Name required");
        Name = name;
    }

    public void Deactivate() => IsActive = false;

    private void RecalculateCapacity()
    {
        TotalCapacity = _seats.Count(s => s.Status == SeatStatus.Active);
    }
}