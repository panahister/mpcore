namespace MPCore.Application.Messaging;

/// <summary>Marks a state-changing application message that returns no value.</summary>
public interface ICommand;

/// <summary>Marks a state-changing application message that returns a value.</summary>
/// <typeparam name="TResponse">The response the command produces.</typeparam>
public interface ICommand<out TResponse>;

/// <summary>Marks a side-effect-free application message that returns a value.</summary>
/// <typeparam name="TResponse">The response the query produces.</typeparam>
public interface IQuery<out TResponse>;
