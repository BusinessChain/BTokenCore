using System;
using System.Collections.Generic;
using System.Linq;

namespace BTokenLib
{
  public class HeaderDownload
  {
    public List<Header> Locator;

    public Header HeaderTip;
    public Header HeaderRoot;
    public Header HeaderAncestor;


    public HeaderDownload(List<Header> locator)
    {
      Locator = locator;
    }

    public virtual void InsertHeader(Header header)
    {
      if (HeaderRoot == null)
      {
        if (HeaderAncestor == null)
        {
          HeaderAncestor = Locator.Find(
            h => h.Hash.IsEqual(header.HashPrevious));

          if (HeaderAncestor == null)
            throw new ProtocolException(
              $"Header {header} does not connect to locator.");
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
        HeaderTip.HeaderNext = header;
        HeaderTip = header;
      }
    }

    public override string ToString()
    {
      return $"{Locator.First()} ... {Locator.Last()}";
    }
  }
}
