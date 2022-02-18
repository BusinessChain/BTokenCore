using System;
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
      public BlockDownload BlockDownloadIndexPrevious;
      public Peer Peer;

      public List<Header> Headers = new();
      public int IndexHeaders;
      public List<Block> Blocks = new();

      public Stopwatch StopwatchBlockDownload = new();

      public long TimeBlockDownloadCompletion;

      public int CountBytes;

      const int COUNT_BLOCK_MAX = 10;



      public BlockDownload(Token token)
      {
        for (int i = 0; i < COUNT_BLOCK_MAX; i += 1)
        {
          Block block = token.CreateBlock(0x400000);
          Blocks.Add(block);
        }
      }

      public void LoadHeaders(ref Header headerLoad)
      {
        int countHeadersNew = Headers.Count *
          (int)(TIMEOUT_RESPONSE_MILLISECONDS /
          (double)StopwatchBlockDownload.ElapsedMilliseconds);

        countHeadersNew = countHeadersNew > COUNT_BLOCK_MAX ?
          COUNT_BLOCK_MAX : countHeadersNew;

        Headers.Clear();
        IndexHeaders = 0;
        CountBytes = 0;

        do
        {
          Headers.Add(headerLoad);
          headerLoad = headerLoad.HeaderNext;
        } while (
        Headers.Count < countHeadersNew
        && headerLoad != null);
      }

      public Block GetBlockToParse()
      {
        if(IsComplete())
        {
          IndexHeaders = 0;
        }

        return Blocks[IndexHeaders];
      }

      public void Parse()
      {
        Block block = Blocks[IndexHeaders];

        block.Parse();

        if (!block.Header.Hash.IsEqual(
          Headers[IndexHeaders].Hash))
        {
          throw new ProtocolException(
            $"Unexpected block {block} in blockLoad {Index}. \n" +
            $"Excpected {Headers[IndexHeaders]}.");
        }

        IndexHeaders += 1;
        CountBytes += block.Header.CountBlockBytes;

        if (IsComplete())
        {
          TimeBlockDownloadCompletion = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
          StopwatchBlockDownload.Stop();
        }
      }

      public bool IsComplete() => 
        IndexHeaders == Headers.Count;
    }
  }
}
