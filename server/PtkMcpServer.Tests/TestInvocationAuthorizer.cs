namespace PtkMcpServer.Tests;

internal sealed class TestInvocationAuthorizer(
    Func<ExecutionPlan, CancellationToken, ValueTask<bool>> authorizePlan,
    Func<ExecutionDispatch, CancellationToken, ValueTask<bool>>? authorizeDispatch = null,
    Func<ExecutionDispatch, CancellationToken, ValueTask<bool>>? recordValidatorStarted = null,
    Func<ExecutionDispatch, BashSyntaxValidationResult, CancellationToken, ValueTask<bool>>?
        recordValidatorCompleted = null)
    : IInvocationAuthorizer
{
    private readonly Func<ExecutionPlan, CancellationToken, ValueTask<bool>> _authorizePlan =
        authorizePlan ?? throw new ArgumentNullException(nameof(authorizePlan));

    private readonly Func<ExecutionDispatch, CancellationToken, ValueTask<bool>> _authorizeDispatch =
        authorizeDispatch ?? ((_, _) => ValueTask.FromResult(true));
    private readonly Func<ExecutionDispatch, CancellationToken, ValueTask<bool>>
        _recordValidatorStarted =
            recordValidatorStarted ?? ((_, _) => ValueTask.FromResult(true));
    private readonly Func<ExecutionDispatch, BashSyntaxValidationResult, CancellationToken, ValueTask<bool>>
        _recordValidatorCompleted =
            recordValidatorCompleted ?? ((_, _, _) => ValueTask.FromResult(true));

    public ValueTask<bool> AuthorizePlanAsync(
        ExecutionPlan plan,
        CancellationToken cancellationToken) =>
        _authorizePlan(plan, cancellationToken);

    public ValueTask<bool> AuthorizeDispatchAsync(
        ExecutionDispatch dispatch,
        CancellationToken cancellationToken) =>
        _authorizeDispatch(dispatch, cancellationToken);

    public ValueTask<bool> RecordValidatorStartedAsync(
        ExecutionDispatch dispatch,
        CancellationToken cancellationToken) =>
        _recordValidatorStarted(dispatch, cancellationToken);

    public ValueTask<bool> RecordValidatorCompletedAsync(
        ExecutionDispatch dispatch,
        BashSyntaxValidationResult result,
        CancellationToken cancellationToken) =>
        _recordValidatorCompleted(dispatch, result, cancellationToken);
}
