namespace BTokenCore;

public static class ActionExtensionMethods
{
  public static void Announce<T>(this Action<T> listeners, T argument)
  {
    if (listeners == null)
      return;

    foreach (Action<T> listener in listeners.GetInvocationList())
      try
      {
        listener(argument);
      }
      catch (Exception exception)
      {
        Console.WriteLine(
          $"Listener {listener.Method.DeclaringType.Name}.{listener.Method.Name} " +
          $"failed on {argument}: {exception}");
      }
  }
}
