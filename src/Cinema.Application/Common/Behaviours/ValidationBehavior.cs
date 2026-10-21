using FluentValidation;
using MediatR;
using Cinema.Domain.Shared;
using System.Reflection;

namespace Cinema.Application.Common.Behaviours;

public class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (!_validators.Any())
        {
            return await next();
        }

        var context = new ValidationContext<TRequest>(request);

        var validationResults = await Task.WhenAll(
            _validators.Select(v => v.ValidateAsync(context, cancellationToken)));

        var failures = validationResults
            .Where(r => r.Errors.Any())
            .SelectMany(r => r.Errors)
            .ToList();

        if (failures.Any())
        {
            return CreateValidationResult(failures);
        }

        return await next();
    }
    
    private TResponse CreateValidationResult(List<FluentValidation.Results.ValidationFailure> failures)
    {
        Type type = typeof(TResponse);

        if (type == typeof(Result) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Result<>)))
        {
            FluentValidation.Results.ValidationFailure firstFailure = failures.First();
            Error error = new(
                Code: "Validation." + firstFailure.PropertyName,
                Description: firstFailure.ErrorMessage
            );

            if (type == typeof(Result))
                return (TResponse)(object)Result.Failure(error);

            MethodInfo failureMethod = typeof(Result).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method => method.Name == nameof(Result.Failure) && method.IsGenericMethodDefinition)
                .MakeGenericMethod(type.GenericTypeArguments[0]);
            return (TResponse)failureMethod.Invoke(null, [error])!;
        }
        throw new ValidationException(failures);
    }
}
