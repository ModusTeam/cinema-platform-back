using Cinema.Application.Technologies.Dtos;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Technologies.Queries.GetAllTechnologies;

public record GetAllTechnologiesQuery : IRequest<Result<List<TechnologyDto>>>;