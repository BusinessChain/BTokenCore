using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

namespace BTokenLib
{
  public partial class Network
  {
    class HeaderDownload
    {
      public Peer Peer;
      public List<Header> Locator;

      public Header HeaderTip;
      public Header HeaderRoot;
      public Header HeaderAncestor;
      public Header HeaderInsertedLast;


      public HeaderDownload(
        List<Header> locator, Peer peer)
      {
        Peer = peer;
        Locator = locator;
      }

      public void InsertHeader(Header header, Token token)
      {
        HeaderInsertedLast = header;

        if (HeaderRoot == null)
        {
          if(HeaderAncestor == null)
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
          HeaderTip.HeaderNext = header;
          HeaderTip = header;

          token.ValidateHeader(HeaderTip);
        }
      }

      public override string ToString()
      {
        return $"{Locator.First()} ... {Locator.Last()}";
      }
    }
  }
}
