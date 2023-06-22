using System;
using System.IO;
using System.Linq;
using System.Threading;


namespace BTokenLib
{
  public class BlockArchiver
  {
    string PathBlockArchive;
    const int COUNT_MAX_BLOCKS_ARCHIVED = 2016;


    public BlockArchiver(string nameToken)
    {
      PathBlockArchive = Path.Combine(nameToken, "blocks");
      Directory.CreateDirectory(PathBlockArchive);
    }

    public bool TryLoadBlockArchive(
      int blockHeight,
      out byte[] buffer)
    {
      buffer = null;

      string pathBlockArchive = Path.Combine(
        PathBlockArchive,
        blockHeight.ToString());

      while (true)
      {
        try
        {
          buffer = File.ReadAllBytes(pathBlockArchive);
          return true;
        }
        catch (FileNotFoundException ex)
        {
          return false;
        }
        catch (Exception ex)
        {
          Console.WriteLine($"{ex.GetType().Name} when attempting to load file {pathBlockArchive}: {ex.Message}.\n" +
            $"Retry in 10 seconds.");

          Thread.Sleep(10000);
        }
      }
    }

    public void ArchiveBlock(Block block)
    {
      string pathFile = Path.Combine(
        PathBlockArchive, 
        block.Header.Height.ToString());

      while (true)
        try
        {
          File.WriteAllBytes(
            pathFile,
            block.Buffer.Take(block.Header.CountBytesBlock).ToArray());

          File.Delete(Path.Combine(
            PathBlockArchive,
            (block.Header.Height - COUNT_MAX_BLOCKS_ARCHIVED).ToString()));

          break;
        }
        catch (Exception ex)
        {
          Console.WriteLine(
            $"{ex.GetType().Name} when writing block height {block.Header.Height} to file:\n" +
            $"{ex.Message}\n " +
            $"Try again in 10 seconds ...");

          Thread.Sleep(10000);
        }
    }

    public void CleanAfterBlockHeight(int height)
    {
      foreach (string file in Directory.GetFiles(PathBlockArchive))
        if (!int.TryParse(Path.GetFileName(file), out int blockHeight) || blockHeight > height)
          File.Delete(file);
    }
  }
}
