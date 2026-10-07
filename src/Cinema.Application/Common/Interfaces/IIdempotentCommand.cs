using System;

namespace Cinema.Application.Common.Interfaces;

public interface IIdempotentCommand
{
    Guid RequestId { get; } 
}