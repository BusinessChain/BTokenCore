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
      public List<Header> Locator;

      public int CountHeaders;
      public Header HeaderTip;
      public Header HeaderRoot;
      public Header HeaderLocatorAncestor;
      public Header HeaderInsertedLast;
      public bool IsFork;


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

          if (HeaderLocatorAncestor != Locator.First())
          {
            IsFork = true;

            Debug.WriteLine(
              $"{header.Hash.ToHexString()} is fork.");
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

          header.HeaderPrevious = HeaderLocatorAncestor;

          HeaderRoot = header;
        }
        else 
        {
          if (!HeaderTip.Hash.IsEqual(header.HashPrevious))
          {
            throw new ProtocolException(
            string.Format(
              "Header insertion out of order. " +
              "Previous header {0}\n Next header: {1}",
              HeaderTip.Hash.ToString(),
              header.HashPrevious.ToString()));
          }

          header.HeaderPrevious = HeaderTip;
          HeaderTip.HeaderNext = header;
        }

        token.ValidateHeader(header);

        HeaderTip = header;
        CountHeaders += 1;
      }



      public void Reset()
      {
        CountHeaders = 0;
        HeaderTip = null;
        HeaderRoot = null;
        HeaderLocatorAncestor = null;
        HeaderInsertedLast = null;
        IsFork = false;
      }
    }
  }
}
