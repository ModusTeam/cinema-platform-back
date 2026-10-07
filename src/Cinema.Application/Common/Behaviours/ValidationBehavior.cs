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
        var type = typeof(TResponse);
        
        if (type == typeof(Result) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Result<>)))
        {
            var firstFailure = failures.First();
            var error = new Error(
                Code: "Validation." + firstFailure.PropertyName,
                Description: firstFailure.ErrorMessage
            );

            var failureMethod = type.GetMethod("Failure", BindingFlags.Public | BindingFlags.Static, new[] { typeof(Error) });

            if (failureMethod != null)
            {
                var result = failureMethod.Invoke(null, new object[] { error });
                return (TResponse)result!;
            }
        }
        throw new ValidationException(failures);
    }
}