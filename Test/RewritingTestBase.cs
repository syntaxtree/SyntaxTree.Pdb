using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Cci.Pdb;
using Mono.Cecil;
using NUnit.Framework;

namespace Pdb.Rewriter.Test
{
	public abstract class RewritingTestBase
	{
		private string tempPath;
		private ModuleDefinition module;

		internal static void AssertFunction(PdbFunction originalFunction, PdbFunction function)
		{
			AssertLines(originalFunction.lines, function.lines);
			AssertScopes(originalFunction.scopes, function.scopes);
			AssertConstants(originalFunction.constants, function.constants);
			Assert.AreEqual(originalFunction.slotToken, function.slotToken);
			AssertSlots(originalFunction.slots, function.slots);
		}

		private static void AssertLines(PdbLines[] originalLines, PdbLines[] lines)
		{
			AssertArrays(originalLines, lines);

			for (int i = 0; i < originalLines.Length; i++)
			{
				AssertFiles(originalLines[i].file, lines[i].file);
				AssertLines(originalLines[i].lines, lines[i].lines);
			}
		}

		private static void AssertLines(PdbLine[] originalLines, PdbLine[] lines)
		{
			AssertArrays(originalLines, lines);

			for (int i = 0; i < originalLines.Length; i++)
			{
				Assert.AreEqual(originalLines[i].lineBegin, lines[i].lineBegin);
				Assert.AreEqual(originalLines[i].lineEnd, lines[i].lineEnd);
				Assert.AreEqual(originalLines[i].colBegin, lines[i].colBegin);
				Assert.AreEqual(originalLines[i].colEnd, lines[i].colEnd);
			}
		}

		private static void AssertFiles(PdbSource originalFile, PdbSource file)
		{
			Assert.AreEqual(originalFile.name, file.name);
			Assert.AreEqual(originalFile.language, file.language);
			Assert.AreEqual(originalFile.vendor, file.vendor);
			Assert.AreEqual(originalFile.doctype, file.doctype);
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

		internal void RunTest(string name, out PdbFunction original, out PdbFunction rewritten, Dictionary<string, string> mapping = null)
		{
			PdbFunction[] originalFunctions;
			using (var file = File.OpenRead(Extensions.GetPdbFileName(module)))
				originalFunctions = PdbFile.LoadFunctions(file, readAllStrings: true);

			var method = Enumerable.Single<MethodDefinition>(module.GetType(typeof (RewritingTest).FullName).Methods, m => m.Name == name);
			original = originalFunctions.Single(f => f.token == method.MetadataToken.ToUInt32());

			Rewrite.MapSymbols(module, mapping ?? new Dictionary<string, string>());

			PdbFunction[] rewrittenFunctions;
			using (var file = File.OpenRead(Extensions.GetPdbFileName(module)))
				rewrittenFunctions = PdbFile.LoadFunctions(file, readAllStrings: true);

			rewritten = rewrittenFunctions.Single(f => f.token == method.MetadataToken.ToUInt32());
		}

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
