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
	}
}
