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
      List<byte[]> trailHashesAnchor) 
      : base(locator)
    {
      TrailHashesAnchor = trailHashesAnchor;
    }

    public new void InsertHeader(Header header, out bool flagRequestNoMoreHeaders)
    {
      flagRequestNoMoreHeaders = false;
      HeaderInsertedLast = header;

      if (HeaderRoot == null)
      {
        if (HeaderAncestor == null)
        {
          HeaderAncestor = Locator.Find(
            h => h.Hash.IsEqual(header.HashPrevious));

          if (HeaderAncestor == null)
            throw new ProtocolException(
              "Header does not connect to locator.");
        }

        if (HeaderAncestor.HeaderNext != null &&
          HeaderAncestor.HeaderNext.Hash.IsEqual(header.Hash))
        {
          if (Locator.Any(h => h.Hash.IsEqual(header.Hash)))
            throw new ProtocolException(
              "Received redundant headers from peer.");

          HeaderAncestor = HeaderAncestor.HeaderNext;
          return;
        }

        header.AppendToHeader(HeaderAncestor);
        HeaderRoot = header;
        HeaderTip = header;
      }
      else
      {
        header.AppendToHeader(HeaderTip);

        while (!header.Hash.IsEqual(TrailHashesAnchor[IndexTrail++]))
          if (IndexTrail == TrailHashesAnchor.Count)
            throw new ProtocolException(
              $"Header hash {header} not equal to anchor " +
              $"trail hash {TrailHashesAnchor[IndexTrail].ToHexString()}.");

        if (IndexTrail == TrailHashesAnchor.Count)
          flagRequestNoMoreHeaders = true;

        HeaderTip.HeaderNext = header;
        HeaderTip = header;
      }
    }
  }
}
