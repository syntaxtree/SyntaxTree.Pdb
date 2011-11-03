using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Cci.Pdb;
using Mono.Cecil;
using NUnit.Framework;

namespace Pdb.Rewriter.Test
{
	[TestFixture]
	public class RewritingTest
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

		private static void AssertFunction(PdbFunction originalFunction, PdbFunction function)
		{
			AssertScopes(originalFunction.scopes, function.scopes);
			AssertConstants(originalFunction.constants, function.constants);
			Assert.AreEqual(originalFunction.slotToken, function.slotToken);
			AssertSlots(originalFunction.slots, function.slots);
		}

		private static void AssertScopes(PdbScope[] originalScopes, PdbScope[] scopes)
		{
			AssertArrays(originalScopes, scopes);

			for (int i = 0; i < originalScopes.Length; i++)
			{
				Assert.AreEqual(originalScopes[i].offset, scopes[i].offset);
				Assert.AreEqual(originalScopes[i].length, scopes[i].length);

				AssertSlots(originalScopes[i].slots, scopes[i].slots);
				AssertConstants(originalScopes[i].constants, scopes[i].constants);

				AssertScopes(originalScopes[i].scopes, scopes[i].scopes);
			}
		}

		private static void AssertConstants(PdbConstant[] originalConstants, PdbConstant[] constants)
		{
			AssertArrays(originalConstants, constants);

			for (int i = 0; i < originalConstants.Length; i++)
			{
				Assert.AreEqual(originalConstants[i].name, constants[i].name);
				Assert.AreEqual(originalConstants[i].token, constants[i].token);
				Assert.AreEqual(originalConstants[i].value, constants[i].value);
			}
		}

		private static void AssertSlots(PdbSlot[] originalSlots, PdbSlot[] slots)
		{
			AssertArrays(originalSlots, slots);

			for (int i = 0; i < originalSlots.Length; i++)
			{
				Assert.AreEqual(originalSlots[i].name, slots[i].name);
				Assert.AreEqual(originalSlots[i].slot, slots[i].slot);
			}
		}

		private static void AssertArrays(Array original, Array array)
		{
			Assert.IsNotNull(original);
			Assert.IsNotNull(array);

			Assert.AreEqual(original.Length, array.Length);
		}

		private void RunTest(string name, out PdbFunction original, out PdbFunction rewritten, Dictionary<string, string> mapping = null)
		{
			PdbFunction[] originalFunctions;
			using (var file = File.OpenRead(module.GetPdbFileName()))
				originalFunctions = PdbFile.LoadFunctions(file, readAllStrings: true);

			var method = module.GetType(typeof (RewritingTest).FullName).Methods.Single(m => m.Name == name);
			original = originalFunctions.Single(f => f.token == method.MetadataToken.ToUInt32());

			Rewrite.MapSymbols(module, mapping ?? new Dictionary<string, string>());

			PdbFunction[] rewrittenFunctions;
			using (var file = File.OpenRead(module.GetPdbFileName()))
				rewrittenFunctions = PdbFile.LoadFunctions(file, readAllStrings: true);

			rewritten = rewrittenFunctions.Single(f => f.token == method.MetadataToken.ToUInt32());
		}

		private string tempPath;
		private ModuleDefinition module;

		[SetUp]
		public void SetupRewritingTest()
		{
			tempPath = Path.GetTempFileName();
			File.Delete(tempPath);
			Directory.CreateDirectory(tempPath);

			var moduleFile = new Uri(typeof(RewritingTest).Assembly.CodeBase).LocalPath;
			var tempModule = Path.Combine(tempPath, Path.GetFileName(moduleFile));
			File.Copy(moduleFile, tempModule);
			File.Copy(Path.ChangeExtension(moduleFile, ".pdb"), Path.Combine(tempPath, Path.GetFileNameWithoutExtension(moduleFile) + ".pdb"));

			module = ModuleDefinition.ReadModule(tempModule);
		}

		[TearDown]
		public void TearDownRewritingTest()
		{
			Directory.Delete(tempPath, recursive: true);
		}		
	}
}
