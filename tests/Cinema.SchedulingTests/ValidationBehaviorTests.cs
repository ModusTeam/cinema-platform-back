using Cinema.Application.Common.Behaviours;
using Cinema.Domain.Shared;
using FluentAssertions;
using FluentValidation;
using Xunit;

namespace Cinema.SchedulingTests;

public class ValidationBehaviorTests
{
    private record Request(string Name);

    private class Validator : AbstractValidator<Request>
    {
        public Validator() => RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required.");
    }

    [Fact]
    public async Task Result_failure_has_expected_code_and_message_and_does_not_call_handler()
    {
        bool called = false;
        var behavior = new ValidationBehavior<Request, Result>([new Validator()]);
        Result result = await behavior.Handle(new Request(""), _ =>
        {
            called = true;
            return Task.FromResult(Result.Success());
        }, TestContext.Current.CancellationToken);

        called.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(new Error("Validation.Name", "Name is required."));
    }

    [Fact]
    public async Task Generic_result_failure_has_expected_code_and_message_and_does_not_call_handler()
    {
        bool called = false;
        var behavior = new ValidationBehavior<Request, Result<Guid>>([new Validator()]);
        Result<Guid> result = await behavior.Handle(new Request(""), _ =>
        {
            called = true;
            return Task.FromResult(Result.Success(Guid.NewGuid()));
        }, TestContext.Current.CancellationToken);

        called.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(new Error("Validation.Name", "Name is required."));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Successful_validation_or_no_validators_reaches_handler(bool useValidator)
    {
        int calls = 0;
        var behavior = new ValidationBehavior<Request, Result<int>>(useValidator ? [new Validator()] : []);
        Result<int> result = await behavior.Handle(new Request("Hall"), _ =>
        {
            calls++;
            return Task.FromResult(Result.Success(42));
        }, TestContext.Current.CancellationToken);

        calls.Should().Be(1);
        result.Value.Should().Be(42);
    }

    [Fact]
    public async Task Non_result_responses_preserve_validation_exception_behavior()
    {
        var behavior = new ValidationBehavior<Request, string>([new Validator()]);
        Func<Task> act = async () => await behavior.Handle(new Request(""),
            _ => throw new InvalidOperationException("Handler must not run"), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<ValidationException>();
    }
}
