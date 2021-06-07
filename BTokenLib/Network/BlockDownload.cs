﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;


namespace BTokenLib
{
  partial class Network
  {
    class BlockDownload
    {
      public int Index;
      public Peer Peer;

      public List<Header> HeadersExpected = new List<Header>();
      public int IndexHeaderExpected;
      public List<Block> Blocks = new List<Block>();
      public bool IsDownloadCompleted;




      public BlockDownload(int index, Peer peer)
      {
        Index = index;
        Peer = peer;
      }

      public void LoadHeaders(
        ref Header headerLoad,
        int countMax)
      {
        do
        {
          HeadersExpected.Add(headerLoad);
          headerLoad = headerLoad.HeaderNext;
        } while (
        HeadersExpected.Count < countMax
        && headerLoad != null);
      }

      public void InsertBlock(Block block)
      {
        if (!block.Header.Hash.IsEqual(
          HeadersExpected[IndexHeaderExpected].Hash))
        {
          throw new ProtocolException(string.Format(
            "Unexpected block header {0} in blockLoad {1}. \n" +
            "Excpected {2}.",
            block.Header.Hash.ToHexString(),
            Index,
            HeadersExpected[IndexHeaderExpected].Hash.ToHexString()));
        }

        Blocks.Add(block);

        IndexHeaderExpected += 1;

        IsDownloadCompleted =
          IndexHeaderExpected == HeadersExpected.Count;
      }
    }
  }
}