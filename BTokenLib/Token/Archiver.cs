using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;


namespace BTokenLib
{
  public class BlockArchiver
  {
    string PathBlockArchive;
    const int COUNT_MAX_BLOCKS_ARCHIVED = 2016;

    FileStream FileBlockArchive;

    public int IndexBlockArchiveInsert;


    public BlockArchiver(string nameToken)
    {
      PathBlockArchive = Path.Combine(nameToken, "blocks");
      Directory.CreateDirectory(PathBlockArchive);
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

    public void ArchiveBlock(Block block)
    {
      while (true)
        try
        {
          File.WriteAllBytes(
            Path.Combine(PathBlockArchive, block.Header.Height.ToString()),
            block.Buffer);

          File.Delete(Path.Combine(
            PathBlockArchive,
            (block.Header.Height - COUNT_MAX_BLOCKS_ARCHIVED).ToString()));

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
  }
}
