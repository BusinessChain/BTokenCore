using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenLib
{
  public abstract partial class Token
  {
    public interface IParser
    {

      Block ParseBlock(
        byte[] buffer,
        ref int startIndex);

      Block ParseBlock(
        byte[] buffer);
    }
  }
}
