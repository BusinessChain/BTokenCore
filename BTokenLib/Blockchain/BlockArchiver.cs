using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace BTokenLib
{
  public partial class Blockchain
  {
    public partial class BlockArchiver
    {
      Token Token;
      Blockchain Blockchain;

      int SIZE_BLOCK_ARCHIVE_BYTES = 0x1000000;
      int COUNT_LOADER_TASKS = Math.Min(Environment.ProcessorCount - 1, 6);

      const int UTXOIMAGE_INTERVAL_LOADER = 100;

      bool IsLoaderFail;
      bool FlagLoaderExit;

      string PathBlockArchive;
      FileStream FileBlockArchive;

      Dictionary<int, Thread> ThreadsSleeping = new();
      Dictionary<int, BlockLoad> QueueBlockLoads = new();
      int CountBytesArchive;
      ConcurrentBag<BlockLoad> PoolBlockLoad = new();

      object LOCK_IndexBlockLoadInsert = new();
      public int IndexBlockArchiveInsert;

      object LOCK_IndexBlockArchiveLoad = new();
      int IndexBlockArchiveLoad;

      byte[] HashStopLoading;


      public BlockArchiver(
        Blockchain blockchain,
        Token token,
        string path)
      {
        Token = token;
        Blockchain = blockchain;

        PathBlockArchive = path;
        Directory.CreateDirectory(PathBlockArchive);
      }

      public bool TryLoadBlocks(
        int indexBlockArchiveLoad,
        byte[] hashStopLoading)
      {
        QueueBlockLoads.Clear();
        ThreadsSleeping.Clear();

        IndexBlockArchiveLoad = indexBlockArchiveLoad;
        IndexBlockArchiveInsert = indexBlockArchiveLoad;
        HashStopLoading = hashStopLoading;
        IsLoaderFail = false;
        FlagLoaderExit = false;

        if (FileBlockArchive != null)
          FileBlockArchive.Dispose();

        Parallel.For(
          0,
          COUNT_LOADER_TASKS,
          i => StartLoader());

        return !IsLoaderFail;
      }


      void StartLoader()
      {
        BlockLoad blockLoad = new(Token);

        while (true)
        {
          lock (LOCK_IndexBlockArchiveLoad)
            blockLoad.Initialize(IndexBlockArchiveLoad++);

          try
          {
            blockLoad.Parse(LoadBlockArchive(blockLoad.Index));
          }
          catch (FileNotFoundException)
          {
            blockLoad.IsInvalid = true;
          }
          catch (ProtocolException ex)
          {
            blockLoad.IsInvalid = true;

            Debug.WriteLine($"ProtocolException when loading " +
              $"blockArchive {blockLoad.Index}:\n {ex.Message}");
          }

          bool flagPutThreadToSleep = false;

        LABEL_PutThreadToSleep:

          if (flagPutThreadToSleep)
          {
            try
            {
              Thread.Sleep(Timeout.Infinite);
            }
            catch (ThreadInterruptedException)
            {
              if (FlagLoaderExit)
                return;

              flagPutThreadToSleep = false;
            }
          }

          lock (LOCK_IndexBlockLoadInsert)
          {
            if (blockLoad.Index != IndexBlockArchiveInsert)
            {
              if (
                QueueBlockLoads.Count < COUNT_LOADER_TASKS ||
                QueueBlockLoads.Keys.Any(k => k > blockLoad.Index))
              {
                QueueBlockLoads.Add(blockLoad.Index, blockLoad);

                if (!PoolBlockLoad.TryTake(out blockLoad))
                  blockLoad = new(Token);

                continue;
              }

              if (FlagLoaderExit)
                return;

              ThreadsSleeping.Add(
                blockLoad.Index,
                Thread.CurrentThread);

              flagPutThreadToSleep = true;
              goto LABEL_PutThreadToSleep;
            }
          }

          if (
            blockLoad.IsInvalid ||
            blockLoad.Blocks.Count == 0)
          {
            CreateBlockArchive(blockLoad.Index);
            break;
          }

          try
          {
            BlockLoadInsert(blockLoad);
          }
          catch (ProtocolException)
          {
            CreateBlockArchive(blockLoad.Index);
            IsLoaderFail = true;
            break;
          }
          catch(Exception ex)
          {
            ($"Unhandled {ex.GetType().Name} when " +
              $"inserting blockload {blockLoad.Index}.\n{ex.Message}")
              .Log(Blockchain.LogFile);
          }

          if (blockLoad.CountBytes < SIZE_BLOCK_ARCHIVE_BYTES)
          {
            CountBytesArchive = blockLoad.CountBytes;

            OpenBlockArchive(blockLoad.Index);
            break;
          }

          if (blockLoad.Index % UTXOIMAGE_INTERVAL_LOADER == 0)
            Blockchain.CreateImage(++blockLoad.Index);

          lock (LOCK_IndexBlockLoadInsert)
          {
            IndexBlockArchiveInsert += 1;

            if (QueueBlockLoads.ContainsKey(IndexBlockArchiveInsert))
            {
              PoolBlockLoad.Add(blockLoad);

              blockLoad = QueueBlockLoads[IndexBlockArchiveInsert];
              QueueBlockLoads.Remove(IndexBlockArchiveInsert);

              goto LABEL_PutThreadToSleep;
            }

            if (ThreadsSleeping.TryGetValue(
              IndexBlockArchiveInsert,
              out Thread threadSleeping))
            {
              ThreadsSleeping.Remove(IndexBlockArchiveInsert);
              threadSleeping.Interrupt();
            }
          }
        }

        lock (LOCK_IndexBlockLoadInsert)
        {
          FlagLoaderExit = true;

          foreach (Thread threadSleeping in ThreadsSleeping.Values)
            threadSleeping.Interrupt();
        }

        ThreadsSleeping.Clear();
        PoolBlockLoad.Clear();
      }

      void BlockLoadInsert(BlockLoad blockLoad)
      {
        int index = 0;

        foreach (Block block in blockLoad.Blocks)
        {
          Blockchain.InsertHeader(block.Header);

          Token.InsertBlock(block);

          if (block.Header.Hash.IsEqual(HashStopLoading))
          {
            SetLengthFileBlockArchive(index);
            break;
          }

          index += block.Header.CountBlockBytes;
        }

        Debug.WriteLine(
          $"Loaded blockchain height: {Blockchain.HeaderTip.Height}, " +
          $"blockload Index: {blockLoad.Index}");
      }

      public byte[] LoadBlockArchive(int index)
      {
        string pathBlockArchive = Path.Combine(
          PathBlockArchive,
          index.ToString());

        while (true)
        {
          try
          {
            return File.ReadAllBytes(pathBlockArchive);
          }
          catch (FileNotFoundException ex)
          {
            throw ex;
          }
          catch (Exception ex)
          {
            if (
              FileBlockArchive != null &&
              FileBlockArchive.Name == pathBlockArchive)
            {
              byte[] buffer = new byte[FileBlockArchive.Length];
              FileBlockArchive.Position = 0;
              FileBlockArchive.Read(buffer, 0, buffer.Length);
              return buffer;
            }

            Console.WriteLine($"{ex.GetType().Name}: {ex.Message}.\n" +
              $"Retry to load block archive {pathBlockArchive} in 10 seconds.");

            Thread.Sleep(10000);
          }
        }
      }

      public bool ArchiveBlockFlagCreateImage(
        Block block,
        int intervalImage)
      {
        block.Header.IndexBlockArchive = IndexBlockArchiveInsert;
        block.Header.StartIndexBlockArchive = (int)FileBlockArchive.Position;

        while (true)
        {
          try
          {
            FileBlockArchive.Write(
              block.Buffer,
              0,
              block.Header.CountBlockBytes);

            FileBlockArchive.Flush();

            break;
          }
          catch (Exception ex)
          {
            Console.WriteLine(
              $"{ex.GetType().Name} when writing to file {FileBlockArchive.Name}:\n" +
              $"{ex.Message}\n " +
              $"Try again in 10 seconds ...");

            Thread.Sleep(10000);
          }
        }

        CountBytesArchive += block.Header.CountBlockBytes;

        if (CountBytesArchive >= SIZE_BLOCK_ARCHIVE_BYTES)
        {
          FileBlockArchive.Dispose();

          IndexBlockArchiveInsert += 1;

          CreateBlockArchive(IndexBlockArchiveInsert);

          return IndexBlockArchiveInsert % intervalImage == 0;
        }

        return false;
      }


      void OpenBlockArchive(int index)
      {
        $"Open BlockArchive {index}.".Log(Blockchain.LogFile);

        string pathFileArchive = Path.Combine(
          PathBlockArchive,
          index.ToString());

        FileBlockArchive = new FileStream(
         pathFileArchive,
         FileMode.Open,
         FileAccess.ReadWrite,
         FileShare.ReadWrite,
         bufferSize: 65536);

        FileBlockArchive.Seek(0, SeekOrigin.End);
      }

      void CreateBlockArchive(int index)
      {
        string pathFileArchive = Path.Combine(
          PathBlockArchive,
          Blockchain.IsFork ? NameFork : "",
          index.ToString());

        FileBlockArchive = new FileStream(
         pathFileArchive,
         FileMode.Create,
         FileAccess.ReadWrite,
         FileShare.ReadWrite,
         bufferSize: 65536);

        CountBytesArchive = 0;
      }

      public void SetLengthFileBlockArchive(int length)
      {
        FileBlockArchive.SetLength(length);
      }

      public void Reorganize()
      {
        string pathImageFork = Path.Combine(
          NameFork,
          NameImage);

        if (Directory.Exists(pathImageFork))
        {
          while (true)
          {
            try
            {
              Directory.Delete(
                NameImage,
                true);

              Directory.Move(
                pathImageFork,
                NameImage);

              break;
            }
            catch (DirectoryNotFoundException)
            {
              break;
            }
            catch (Exception ex)
            {
              Console.WriteLine(
                "{0} when attempting to delete directory:\n{1}",
                ex.GetType().Name,
                ex.Message);

              Thread.Sleep(3000);
            }
          }
        }

        string pathImageForkOld = Path.Combine(
          NameFork,
          NameImage,
          NameImageOld);

        string pathImageOld = Path.Combine(
          NameImage,
          NameImageOld);

        if (Directory.Exists(pathImageForkOld))
        {
          while (true)
          {
            try
            {
              Directory.Delete(
                pathImageOld,
                true);

              Directory.Move(
                pathImageForkOld,
                pathImageOld);

              break;
            }
            catch (DirectoryNotFoundException)
            {
              break;
            }
            catch (Exception ex)
            {
              Console.WriteLine(
                "{0} when attempting to delete directory:\n{1}",
                ex.GetType().Name,
                ex.Message);

              Thread.Sleep(3000);
            }
          }
        }

        string pathBlockArchiveFork = PathBlockArchive + "Fork";
        var dirArchiveFork = new DirectoryInfo(pathBlockArchiveFork);

        string filename = Path.GetFileName(FileBlockArchive.Name);
        FileBlockArchive.Dispose();

        foreach (FileInfo archiveFork in dirArchiveFork.GetFiles())
        {
          archiveFork.MoveTo(PathBlockArchive);
        }

        OpenBlockArchive(IndexBlockArchiveInsert);

        Directory.Delete(pathBlockArchiveFork);
        Blockchain.DismissFork();
      }
    }
  }
}
