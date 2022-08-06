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
    List<byte[]> TrailHashesAnchor;
    int IndexTrail;


    public HeaderDownloadBToken(
      List<Header> locator,
      List<byte[]> trailHashesAnchor,
      int indexTrail) 
      : base(locator)
    {
      TrailHashesAnchor = trailHashesAnchor;
      IndexTrail = indexTrail;
    }

    public override void InsertHeader(
      Header header, 
      out bool flagRequestNoMoreHeaders)
    {
      if (IndexTrail >= TrailHashesAnchor.Count)
      {
        flagRequestNoMoreHeaders = false;
        return;
      }

      if (!TrailHashesAnchor[IndexTrail].Equals(header.Hash))
        throw new ProtocolException(
          $"Header hash {header} not " +
          $"equal to trail {TrailHashesAnchor[IndexTrail].ToHexString()}.");

      IndexTrail += 1;

      base.InsertHeader(
        header,
        out flagRequestNoMoreHeaders);
    }
  }
}
