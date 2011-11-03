using System;
using System.Collections.Generic;
using System.Diagnostics.SymbolStore;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

using Microsoft.Cci.Pdb;

using Mono.Cecil;

namespace Pdb.Rewriter
{
	/// <summary>
	/// Entry point of the pdb rewriter assembly.
	/// </summary>
	public static class Rewrite
	{
		/// <summary>
		/// Rewrite the pdb attached to the module, and remaps the source files
		/// referenced in the pdb
		/// </summary>
		/// <param name="module">The module that the pdb is attached to.</param>
		/// <param name="fileMapping">The mapping between the source files.</param>
		public static void MapSymbols(ModuleDefinition module, IDictionary<string, string> fileMapping)
		{
			if (module == null)
				throw new ArgumentNullException("module");
			if (fileMapping == null)
				throw new ArgumentNullException("fileMapping");

			var pdb = module.GetPdbFileName();
			if (!File.Exists(pdb))
				throw new ArgumentException(string.Format("No pdb associated with module {0}", module.FullyQualifiedName), "module");

			PdbFunction[] functions;
			using (var file = File.OpenRead(pdb))
				functions = PdbFile.LoadFunctions(file, readAllStrings: true);

			ApplyMapping(functions, fileMapping);

			File.Delete(pdb);
			using (var writer = new PdbWriter (module))
				writer.Write(functions);
		}

		private static void ApplyMapping(IEnumerable<PdbFunction> functions, IDictionary<string, string> fileMapping)
		{
			foreach (var document in functions.SelectMany(function => function.lines))
				ApplyMapping(document, fileMapping);
		}

		private static void ApplyMapping(PdbLines document, IDictionary<string, string> fileMapping)
		{
			string mapped;
			if (!fileMapping.TryGetValue(document.file.name, out mapped))
				return;

			document.file.name = mapped;
		}
	}

	static class Extensions
	{
		public static string GetPdbFileName(this ModuleDefinition module)
		{
			return Path.GetFullPath(Path.ChangeExtension(module.FullyQualifiedName, ".pdb"));
		}
	}

	class PdbWriter : IDisposable
	{
		private ISymUnmanagedWriter2 pdb;
		private readonly Metadata metadata;
		private IDictionary<string, ISymUnmanagedDocumentWriter> documentWriters; 

		public PdbWriter(ModuleDefinition module)
		{
			this.pdb = new ISymUnmanagedWriter2();
			this.metadata = new Metadata(module);
			this.documentWriters = new Dictionary<string, ISymUnmanagedDocumentWriter>();
			
			this.pdb.Initialize(this.metadata, module.GetPdbFileName(), pIStream: null, fFullBuild: true);
		}

		public void Write(IEnumerable<PdbFunction> functions)
		{
			foreach (var function in functions)
				Write(function);
		}

		private void Write(PdbFunction function)
		{
			pdb.OpenMethod((int) function.token);

			WriteDocuments(function);
			WriteScopes(function.scopes, function);
			WriteConstants(function.constants);

			// TODO
			// function.iteratorClass
			// function.iteratorScopes
			// function.namespaceScopes
			// function.usedNamespaces
			// function.usingCounts

			pdb.CloseMethod();
		}

		private void WriteConstants(IEnumerable<PdbConstant> constants)
		{
			foreach (var constant in constants)
				WriteConstant(constant);
		}

		private void WriteConstant(PdbConstant constant)
		{
			pdb.DefineConstant2(constant.name, constant.value, (int) constant.token);
		}

		private void WriteScopes(IEnumerable<PdbScope> scopes, PdbFunction function)
		{
			foreach (var scope in scopes)
				WriteScope(scope, function);
		}

		private void WriteScope(PdbScope scope, PdbFunction function)
		{
			pdb.OpenScope((int) scope.offset);

			WriteSlots(scope, function);
			WriteConstants(scope.constants);
			WriteScopes(scope.scopes, function);

			pdb.CloseScope((int) (scope.offset + scope.length));
		}

		private void WriteSlots(PdbScope scope, PdbFunction function)
		{
			foreach (var slot in scope.slots)
				WriteSlot(function.slotToken, slot, scope);
		}

		private void WriteSlot(uint slotToken, PdbSlot slot, PdbScope scope)
		{
			// it doesn't seem like slot.flags is persisted.
			pdb.DefineLocalVariable2(slot.name, slot.flags, (int) slotToken, (int) SymAddressKind.ILOffset, (int) slot.slot, 0, 0, (int) scope.offset, (int) (scope.offset + scope.length));
		}

		private void WriteDocuments(PdbFunction function)
		{
			foreach (var document in function.lines)
				WriteDocument(document);
		}

		private void WriteDocument(PdbLines document)
		{
			var count = document.lines.Length;

			var offsets = new int[count];
			var lines = new int[count];
			var columns = new int[count];
			var endLines = new int[count];
			var endColumns = new int[count];

			for (int i = 0; i < count; i++)
			{
				offsets[i] = (int) document.lines[i].offset;
				lines[i] = (int)document.lines[i].lineBegin;
				columns[i] = document.lines[i].colBegin;
				endLines[i] = (int)document.lines[i].lineEnd;
				endColumns[i] = document.lines[i].colEnd;
			}

			pdb.DefineSequencePoints(UnmanagedDocumentFor(document.file), count, offsets, lines, columns, endLines, endColumns);
		}

		private ISymUnmanagedDocumentWriter UnmanagedDocumentFor(PdbSource file)
		{
			ISymUnmanagedDocumentWriter documentWriter;
			if (documentWriters.TryGetValue(file.name, out documentWriter))
				return documentWriter;

			documentWriter = pdb.DefineDocument(file.name, ref file.language, ref file.vendor, ref file.doctype);
			documentWriters.Add(file.name, documentWriter);
			return documentWriter;
		}

		public void Dispose()
		{
			this.pdb.Close();
			Marshal.ReleaseComObject(this.pdb);

			foreach (var documentWriter in documentWriters.Values)
				Marshal.ReleaseComObject(documentWriter);

			this.documentWriters = null;
			this.pdb = null;
		}
	}

	class Metadata : IMetaDataEmit, IMetaDataImport
	{
		private readonly ModuleDefinition module;

		public Metadata(ModuleDefinition module)
		{
			this.module = module;
		}

		private static void WriteIntPtr(IntPtr ptr, int value)
		{
			if (ptr == IntPtr.Zero)
				return;

			if (IntPtr.Size == 8)
				Marshal.WriteInt64(ptr, value);
			else
				Marshal.WriteInt32(ptr, value);
		}

		private static void WriteString(string str, IntPtr buffer, int bufferSize, IntPtr chars)
		{
			var length = str.Length + 1 >= bufferSize ? bufferSize - 1 : str.Length;
			var offset = 0;

			for (int i = 0; i < length; i++)
			{
				Marshal.WriteInt16(buffer, offset, str[i]);
				offset += 2;
			}

			Marshal.WriteInt16(buffer, offset, 0);
			WriteIntPtr(chars, length + 1);
		}

		public void GetMethodProps(int mb, IntPtr pClass, IntPtr szMethod, int cchMethod, IntPtr pchMethod, IntPtr pdwAttr, IntPtr ppvSigBlob, IntPtr pcbSigBlob, IntPtr pulCodeRVA, IntPtr pdwImplFlags)
		{
			var method = module.LookupToken(mb) as MethodDefinition;
			if (method == null)
			{
				WriteString("", szMethod, cchMethod, pchMethod);
				return;
			}

			WriteString(method.Name, szMethod, cchMethod, pchMethod);
			WriteIntPtr(pdwAttr, (int) method.Attributes);
			WriteIntPtr(pulCodeRVA, method.RVA);
			WriteIntPtr(pdwImplFlags, (int) method.ImplAttributes);
		}

		public void GetTypeDefProps(int td, IntPtr szTypeDef, int cchTypeDef, IntPtr pchTypeDef, IntPtr pdwTypeDefFlags, IntPtr ptkExtends)
		{
			var type = module.LookupToken(td) as TypeDefinition;
			if (type == null)
			{
				WriteString("", szTypeDef, cchTypeDef, pchTypeDef);
				return;
			}

			WriteString(type.FullName, szTypeDef, cchTypeDef, pchTypeDef);
			WriteIntPtr(pdwTypeDefFlags, (int) type.Attributes);
			WriteIntPtr(ptkExtends, type.BaseType == null ? 0 : type.BaseType.MetadataToken.ToInt32());
		}

		public void GetNestedClassProps(int tdNestedClass, IntPtr ptdEnclosingClass)
		{
			var type = module.LookupToken(tdNestedClass) as TypeDefinition;
			if (type == null)
			{
				WriteIntPtr(ptdEnclosingClass, 0);
				return;
			}

			WriteIntPtr(ptdEnclosingClass, type.DeclaringType == null ? 0 : type.DeclaringType.MetadataToken.ToInt32());
		}

		#region Implementation of IMetaDataEmit

		public void __SetModuleProps()
		{
			throw new NotImplementedException();
		}

		public void __Save()
		{
			throw new NotImplementedException();
		}

		public void __SaveToStream()
		{
			throw new NotImplementedException();
		}

		public void __GetSaveSize()
		{
			throw new NotImplementedException();
		}

		public void __DefineTypeDef()
		{
			throw new NotImplementedException();
		}

		public void __DefineNestedType()
		{
			throw new NotImplementedException();
		}

		public void __SetHandler()
		{
			throw new NotImplementedException();
		}

		public void __DefineMethod()
		{
			throw new NotImplementedException();
		}

		public void __DefineMethodImpl()
		{
			throw new NotImplementedException();
		}

		public void __DefineTypeRefByName()
		{
			throw new NotImplementedException();
		}

		public void __DefineImportType()
		{
			throw new NotImplementedException();
		}

		public void __DefineMemberRef()
		{
			throw new NotImplementedException();
		}

		public void __DefineImportMember()
		{
			throw new NotImplementedException();
		}

		public void __DefineEvent()
		{
			throw new NotImplementedException();
		}

		public void __SetClassLayout()
		{
			throw new NotImplementedException();
		}

		public void __DeleteClassLayout()
		{
			throw new NotImplementedException();
		}

		public void __SetFieldMarshal()
		{
			throw new NotImplementedException();
		}

		public void __DeleteFieldMarshal()
		{
			throw new NotImplementedException();
		}

		public void __DefinePermissionSet()
		{
			throw new NotImplementedException();
		}

		public void __SetRVA()
		{
			throw new NotImplementedException();
		}

		public void __GetTokenFromSig()
		{
			throw new NotImplementedException();
		}

		public void __DefineModuleRef()
		{
			throw new NotImplementedException();
		}

		public void __SetParent()
		{
			throw new NotImplementedException();
		}

		public void __GetTokenFromTypeSpec()
		{
			throw new NotImplementedException();
		}

		public void __SaveToMemory()
		{
			throw new NotImplementedException();
		}

		public void __DefineUserString()
		{
			throw new NotImplementedException();
		}

		public void __DeleteToken()
		{
			throw new NotImplementedException();
		}

		public void __SetMethodProps()
		{
			throw new NotImplementedException();
		}

		public void __SetTypeDefProps()
		{
			throw new NotImplementedException();
		}

		public void __SetEventProps()
		{
			throw new NotImplementedException();
		}

		public void __SetPermissionSetProps()
		{
			throw new NotImplementedException();
		}

		public void __DefinePinvokeMap()
		{
			throw new NotImplementedException();
		}

		public void __SetPinvokeMap()
		{
			throw new NotImplementedException();
		}

		public void __DeletePinvokeMap()
		{
			throw new NotImplementedException();
		}

		public void __DefineCustomAttribute()
		{
			throw new NotImplementedException();
		}

		public void __SetCustomAttributeValue()
		{
			throw new NotImplementedException();
		}

		public void __DefineField()
		{
			throw new NotImplementedException();
		}

		public void __DefineProperty()
		{
			throw new NotImplementedException();
		}

		public void __DefineParam()
		{
			throw new NotImplementedException();
		}

		public void __SetFieldProps()
		{
			throw new NotImplementedException();
		}

		public void __SetPropertyProps()
		{
			throw new NotImplementedException();
		}

		public void __SetParamProps()
		{
			throw new NotImplementedException();
		}

		public void __DefineSecurityAttributeSet()
		{
			throw new NotImplementedException();
		}

		public void __ApplyEditAndContinue()
		{
			throw new NotImplementedException();
		}

		public void __TranslateSigWithScope()
		{
			throw new NotImplementedException();
		}

		public void __SetMethodImplFlags()
		{
			throw new NotImplementedException();
		}

		public void __SetFieldRVA()
		{
			throw new NotImplementedException();
		}

		public void __Merge()
		{
			throw new NotImplementedException();
		}

		public void __MergeEnd()
		{
			throw new NotImplementedException();
		}

		#endregion

		#region Implementation of IMetaDataImport

		public void __CloseEnum()
		{
			throw new NotImplementedException();
		}

		public void __CountEnum()
		{
			throw new NotImplementedException();
		}

		public void __ResetEnum()
		{
			throw new NotImplementedException();
		}

		public void __EnumTypeDefs()
		{
			throw new NotImplementedException();
		}

		public void __EnumInterfaceImpls()
		{
			throw new NotImplementedException();
		}

		public void __EnumTypeRefs()
		{
			throw new NotImplementedException();
		}

		public void __FindTypeDefByName()
		{
			throw new NotImplementedException();
		}

		public void __GetScopeProps()
		{
			throw new NotImplementedException();
		}

		public void __GetModuleFromScope()
		{
			throw new NotImplementedException();
		}

		public void __GetInterfaceImplProps()
		{
			throw new NotImplementedException();
		}

		public void __GetTypeRefProps()
		{
			throw new NotImplementedException();
		}

		public void __ResolveTypeRef()
		{
			throw new NotImplementedException();
		}

		public void __EnumMembers()
		{
			throw new NotImplementedException();
		}

		public void __EnumMembersWithName()
		{
			throw new NotImplementedException();
		}

		public void __EnumMethods()
		{
			throw new NotImplementedException();
		}

		public void __EnumMethodsWithName()
		{
			throw new NotImplementedException();
		}

		public void __EnumFields()
		{
			throw new NotImplementedException();
		}

		public void __EnumFieldsWithName()
		{
			throw new NotImplementedException();
		}

		public void __EnumParams()
		{
			throw new NotImplementedException();
		}

		public void __EnumMemberRefs()
		{
			throw new NotImplementedException();
		}

		public void __EnumMethodImpls()
		{
			throw new NotImplementedException();
		}

		public void __EnumPermissionSets()
		{
			throw new NotImplementedException();
		}

		public void __FindMember()
		{
			throw new NotImplementedException();
		}

		public void __FindMethod()
		{
			throw new NotImplementedException();
		}

		public void __FindField()
		{
			throw new NotImplementedException();
		}

		public void __FindMemberRef()
		{
			throw new NotImplementedException();
		}

		public void __GetMemberRefProps()
		{
			throw new NotImplementedException();
		}

		public void __EnumProperties()
		{
			throw new NotImplementedException();
		}

		public void __EnumEvents()
		{
			throw new NotImplementedException();
		}

		public void __GetEventProps()
		{
			throw new NotImplementedException();
		}

		public void __EnumMethodSemantics()
		{
			throw new NotImplementedException();
		}

		public void __GetMethodSemantics()
		{
			throw new NotImplementedException();
		}

		public void __GetClassLayout()
		{
			throw new NotImplementedException();
		}

		public void __GetFieldMarshal()
		{
			throw new NotImplementedException();
		}

		public void __GetRVA()
		{
			throw new NotImplementedException();
		}

		public void __GetPermissionSetProps()
		{
			throw new NotImplementedException();
		}

		public void __GetSigFromToken()
		{
			throw new NotImplementedException();
		}

		public void __GetModuleRefProps()
		{
			throw new NotImplementedException();
		}

		public void __EnumModuleRefs()
		{
			throw new NotImplementedException();
		}

		public void __GetTypeSpecFromToken()
		{
			throw new NotImplementedException();
		}

		public void __GetNameFromToken()
		{
			throw new NotImplementedException();
		}

		public void __EnumUnresolvedMethods()
		{
			throw new NotImplementedException();
		}

		public void __GetUserString()
		{
			throw new NotImplementedException();
		}

		public void __GetPinvokeMap()
		{
			throw new NotImplementedException();
		}

		public void __EnumSignatures()
		{
			throw new NotImplementedException();
		}

		public void __EnumTypeSpecs()
		{
			throw new NotImplementedException();
		}

		public void __EnumUserStrings()
		{
			throw new NotImplementedException();
		}

		public void __GetParamForMethodIndex()
		{
			throw new NotImplementedException();
		}

		public void __EnumCustomAttributes()
		{
			throw new NotImplementedException();
		}

		public void __GetCustomAttributeProps()
		{
			throw new NotImplementedException();
		}

		public void __FindTypeRef()
		{
			throw new NotImplementedException();
		}

		public void __GetMemberProps()
		{
			throw new NotImplementedException();
		}

		public void __GetFieldProps()
		{
			throw new NotImplementedException();
		}

		public void __GetPropertyProps()
		{
			throw new NotImplementedException();
		}

		public void __GetParamProps()
		{
			throw new NotImplementedException();
		}

		public void __GetCustomAttributeByName()
		{
			throw new NotImplementedException();
		}

		public void __IsValidToken()
		{
			throw new NotImplementedException();
		}

		public void __GetNativeCallConvFromSig()
		{
			throw new NotImplementedException();
		}

		public void __IsGlobal()
		{
			throw new NotImplementedException();
		}

		#endregion
	}

	[Guid("7dac8207-d3ae-4c75-9b67-92801a497d44")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	[ComImport]
	interface IMetaDataImport
	{
		void __CloseEnum();
		void __CountEnum();
		void __ResetEnum();
		void __EnumTypeDefs();
		void __EnumInterfaceImpls();
		void __EnumTypeRefs();
		void __FindTypeDefByName();
		void __GetScopeProps();
		void __GetModuleFromScope();

		void GetTypeDefProps(int td, IntPtr szTypeDef, int cchTypeDef, IntPtr pchTypeDef, IntPtr pdwTypeDefFlags, IntPtr ptkExtends);

		void __GetInterfaceImplProps();
		void __GetTypeRefProps();
		void __ResolveTypeRef();
		void __EnumMembers();
		void __EnumMembersWithName();
		void __EnumMethods();
		void __EnumMethodsWithName();
		void __EnumFields();
		void __EnumFieldsWithName();
		void __EnumParams();
		void __EnumMemberRefs();
		void __EnumMethodImpls();
		void __EnumPermissionSets();
		void __FindMember();
		void __FindMethod();
		void __FindField();
		void __FindMemberRef();

		void GetMethodProps(int mb, IntPtr pClass, IntPtr szMethod, int cchMethod, IntPtr pchMethod, IntPtr pdwAttr, IntPtr ppvSigBlob, IntPtr pcbSigBlob, IntPtr pulCodeRVA, IntPtr pdwImplFlags);

		void __GetMemberRefProps();
		void __EnumProperties();
		void __EnumEvents();
		void __GetEventProps();
		void __EnumMethodSemantics();
		void __GetMethodSemantics();
		void __GetClassLayout();
		void __GetFieldMarshal();
		void __GetRVA();
		void __GetPermissionSetProps();
		void __GetSigFromToken();
		void __GetModuleRefProps();
		void __EnumModuleRefs();
		void __GetTypeSpecFromToken();
		void __GetNameFromToken();
		void __EnumUnresolvedMethods();
		void __GetUserString();
		void __GetPinvokeMap();
		void __EnumSignatures();
		void __EnumTypeSpecs();
		void __EnumUserStrings();
		void __GetParamForMethodIndex();
		void __EnumCustomAttributes();
		void __GetCustomAttributeProps();
		void __FindTypeRef();
		void __GetMemberProps();
		void __GetFieldProps();
		void __GetPropertyProps();
		void __GetParamProps();
		void __GetCustomAttributeByName();
		void __IsValidToken();

		void GetNestedClassProps(int tdNestedClass, IntPtr ptdEnclosingClass);

		void __GetNativeCallConvFromSig();
		void __IsGlobal();
	}

	[Guid("ba3fee4c-ecb9-4e41-83b7-183fa41cd859")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	[ComImport]
	interface IMetaDataEmit
	{
		void __SetModuleProps();
		void __Save();
		void __SaveToStream();
		void __GetSaveSize();
		void __DefineTypeDef();
		void __DefineNestedType();
		void __SetHandler();
		void __DefineMethod();
		void __DefineMethodImpl();
		void __DefineTypeRefByName();
		void __DefineImportType();
		void __DefineMemberRef();
		void __DefineImportMember();
		void __DefineEvent();
		void __SetClassLayout();
		void __DeleteClassLayout();
		void __SetFieldMarshal();
		void __DeleteFieldMarshal();
		void __DefinePermissionSet();
		void __SetRVA();
		void __GetTokenFromSig();
		void __DefineModuleRef();
		void __SetParent();
		void __GetTokenFromTypeSpec();
		void __SaveToMemory();
		void __DefineUserString();
		void __DeleteToken();
		void __SetMethodProps();
		void __SetTypeDefProps();
		void __SetEventProps();
		void __SetPermissionSetProps();
		void __DefinePinvokeMap();
		void __SetPinvokeMap();
		void __DeletePinvokeMap();
		void __DefineCustomAttribute();
		void __SetCustomAttributeValue();
		void __DefineField();
		void __DefineProperty();
		void __DefineParam();
		void __SetFieldProps();
		void __SetPropertyProps();
		void __SetParamProps();
		void __DefineSecurityAttributeSet();
		void __ApplyEditAndContinue();
		void __TranslateSigWithScope();
		void __SetMethodImplFlags();
		void __SetFieldRVA();
		void __Merge();
		void __MergeEnd();
	}

	[Guid("b01fafeb-c450-3a4d-beec-b4ceec01e006")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	[ComImport]
	internal interface ISymUnmanagedDocumentWriter
	{
	}

	[Guid("0b97726e-9e6d-4f05-9a26-424022093caa")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	[ComImport]
	[CoClass(typeof(CorSymWriterClass))]
	interface ISymUnmanagedWriter2
	{
		ISymUnmanagedDocumentWriter DefineDocument(string url, ref Guid language, ref Guid languageVendor, ref Guid documentType);
		void __SetUserEntryPoint();
		void OpenMethod(int method);
		void CloseMethod();
		int OpenScope(int startOffset);
		void CloseScope(int endOffset);
		void __SetScopeRange();
		void DefineLocalVariable(string name, int attributes, int cSig, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] signature, int addrKind, int addr1, int addr2, int startOffset, int endOffset);
		void __DefineParameter();
		void __DefineField();
		void __DefineGlobalVariable();
		void Close();
		void __SetSymAttribute();
		void __OpenNamespace();
		void __CloseNamespace();
		void __UsingNamespace();
		void __SetMethodSourceRange();
		void Initialize([MarshalAs(UnmanagedType.IUnknown)] object emitter, string filename, [MarshalAs(UnmanagedType.IUnknown)] object pIStream, bool fFullBuild);

		void __GetDebugInfo();

		void DefineSequencePoints(ISymUnmanagedDocumentWriter document, int spCount,
		  [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] int[] offsets,
		  [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] int[] lines,
		  [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] int[] columns,
		  [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] int[] endLines,
		  [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] int[] endColumns);

		void __RemapToken();
		void __Initialize2();
		void __DefineConstant();
		void __Abort();

		void DefineLocalVariable2(string name, int attributes, int token, int addrKind, int addr1, int addr2, int addr3, int startOffset, int endOffset);

		void __DefineGlobalVariable2();
		void DefineConstant2(string name, object value, int token);
	}

	[Guid("108296c1-281e-11d3-bd22-0000f80849bd")]
	[ComImport]
	class CorSymWriterClass
	{
	}
}
