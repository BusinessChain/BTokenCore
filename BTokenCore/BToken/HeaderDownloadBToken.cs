using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using BTokenLib;

namespace BTokenCore
{
  public class HeaderDownloadBToken : HeaderDownload
  {
    Dictionary<byte[], int> WinningBlockInHeightAnchorBlock;

    public HeaderDownloadBToken(
      List<Header> locator,
      Dictionary<byte[], int> winningBlockInHeightAnchorBlock) 
      : base(locator)
    {
      WinningBlockInHeightAnchorBlock = winningBlockInHeightAnchorBlock;
    }

    public override void InsertHeader(Header header)
    {
      if (!WinningBlockInHeightAnchorBlock.TryGetValue(header.Hash, out int heightBlockAnchor))
      {
        throw new ProtocolException($"Header {header} not anchored in parent chain.");
      }
      else if (header.Height > 1 && heightBlockAnchor < WinningBlockInHeightAnchorBlock[header.HashPrevious])
        throw new ProtocolException(
          $"Header {header} is anchored prior to its previous header {header.HeaderPrevious} in parent chain.");

      base.InsertHeader(header);
    }
  }
}