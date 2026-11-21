namespace BTokenCore;

public class ParameterException : Exception
{
  public readonly List<string> Parameters;


  public ParameterException(string message, params string[] parameters)
      : base(message)
  {
    Parameters = [.. parameters];
  }
}
