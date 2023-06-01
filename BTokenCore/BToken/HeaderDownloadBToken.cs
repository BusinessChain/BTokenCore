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
    Dictionary<byte[], int> WinningBlockInHeightParentBlock;


    public HeaderDownloadBToken(
      List<Header> locator,
      Dictionary<byte[], int> winningBlockInHeightParentBlock) 
      : base(locator)
    {
      WinningBlockInHeightParentBlock = winningBlockInHeightParentBlock;
    }

    public override void InsertHeader(Header header)
    {
      if (!WinningBlockInHeightParentBlock.TryGetValue(header.Hash, out int height))
        throw new ProtocolException(
          $"Header {header} not anchored in parent chain.");
      else if (height < WinningBlockInHeightParentBlock[header.HashPrevious])
        throw new ProtocolException(
          $"Header {header} is anchored prior to its previous header {header.HeaderPrevious} in parent chain.");

      base.InsertHeader(header);
    }
  }
}
