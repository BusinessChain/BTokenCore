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
      public Header HeaderLocatorAncestor;
      public Header HeaderInsertedLast;
      public bool IsFork;


      public HeaderDownload(
        List<Header> locator,
        Peer peer)
      {
        Peer = peer;
        Locator = locator;
      }

      public void InsertHeader(Header header, Token token)
      {
        HeaderInsertedLast = header;

        if (HeaderLocatorAncestor == null)
        {
          HeaderLocatorAncestor = Locator.Find(
            h => h.Hash.IsEqual(header.HashPrevious));

          if (HeaderLocatorAncestor == null)
          {
            throw new ProtocolException(
              "Header does not connect to locator.");
          }
        }

        if (HeaderTip == null)
        {
          if (
            HeaderLocatorAncestor.HeaderNext != null &&
            HeaderLocatorAncestor.HeaderNext.Hash.IsEqual(header.Hash))
          {
            if (Locator.Any(h => h.Hash.IsEqual(header.Hash)))
            {
              throw new ProtocolException(
                "Received redundant headers from peer.");
            }

            HeaderLocatorAncestor = HeaderLocatorAncestor.HeaderNext;
            return;
          }

          HeaderRoot = header;
          HeaderTip = HeaderLocatorAncestor;
        }
        else 
        {
          if (!HeaderTip.Hash.IsEqual(header.HashPrevious))
          {
            throw new ProtocolException(
              $"Header insertion out of order. " +
              $"Previous header {HeaderTip}\n " +
              $"Next header: {header.HeaderPrevious}");
          }
        }

        header.ExtendHeaderTip(ref HeaderTip);

        token.ValidateHeader(HeaderTip);
      }

      public string ToStringLocator()
      {
        return $"{Locator.First()} ... {Locator.Last()}";
      }
    }
  }
}
