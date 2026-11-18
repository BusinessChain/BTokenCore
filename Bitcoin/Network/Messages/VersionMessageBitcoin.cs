using System.Text.RegularExpressions;

namespace BTokenCore;

internal partial class NetworkBitcoin : Network
{
  class VersionMessageBitcoin : VersionMessage
  {
    const int INDEX_USER_AGENT = 80;
    const int VERSION_MAJOR_MIN = 30;

    static readonly Regex REGEX_USER_AGENT = new(@"^/Satoshi:(\d+)\.\d+\.\d+(\([^/]*\))?/$");


    internal override async Task Run(Peer peer)
    {
      int index = INDEX_USER_AGENT;
      string userAgent = VarString.GetString(Payload, ref index);

      Match match = REGEX_USER_AGENT.Match(userAgent);

      if (!match.Success || !int.TryParse(match.Groups[1].Value, out int versionMajor) || versionMajor < VERSION_MAJOR_MIN)
        throw new ProtocolException($"Peer user agent {userAgent} is not Bitcoin Core {VERSION_MAJOR_MIN} or later.");

      await base.Run(peer);
    }
  }
}