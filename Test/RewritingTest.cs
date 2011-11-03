using System.Collections.Generic;
using Microsoft.Cci.Pdb;
using NUnit.Framework;

namespace Pdb.Rewriter.Test
{
	[TestFixture]
	public class RewritingTest : RewritingTestBase
	{
		public int Answer()
		{
			int a = 4;
			int b = 10;

			const int c = 2;

			return a * b + c;
		}

		[Test]
		public void MethodWithLocals()
		{
			PdbFunction original, rewritten;
			RunTest("Answer", out original, out rewritten);
			AssertFunction(original, rewritten);
		}

		public IEnumerable<int> Evens()
		{
			int i = 0;

			while (true)
			{
				yield return i;
				i += 2;
			}
		}

		[Test]
		public void Iterator()
		{
			PdbFunction original, rewritten;
			RunTest("Evens", out original, out rewritten);
			AssertFunction(original, rewritten);
		}
	}
}
