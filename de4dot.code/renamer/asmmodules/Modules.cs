﻿/*
    Copyright (C) 2011-2012 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using dot10.DotNet;
using de4dot.blocks;

namespace de4dot.code.renamer.asmmodules {
	class Modules : IResolver {
		bool initializeCalled = false;
		IDeobfuscatorContext deobfuscatorContext;
		List<Module> modules = new List<Module>();
		Dictionary<ModuleDef, Module> modulesDict = new Dictionary<ModuleDef, Module>();
		AssemblyHash assemblyHash = new AssemblyHash();

		List<MTypeDef> allTypes = new List<MTypeDef>();
		List<MTypeDef> baseTypes = new List<MTypeDef>();
		List<MTypeDef> nonNestedTypes;

		public IList<Module> TheModules {
			get { return modules; }
		}

		public IEnumerable<MTypeDef> AllTypes {
			get { return allTypes; }
		}

		public IEnumerable<MTypeDef> BaseTypes {
			get { return baseTypes; }
		}

		public List<MTypeDef> NonNestedTypes {
			get { return nonNestedTypes; }
		}

		class AssemblyHash {
			IDictionary<string, ModuleHash> assemblyHash = new Dictionary<string, ModuleHash>(StringComparer.Ordinal);

			public void add(Module module) {
				ModuleHash moduleHash;
				var key = getModuleKey(module);
				if (!assemblyHash.TryGetValue(key, out moduleHash))
					assemblyHash[key] = moduleHash = new ModuleHash();
				moduleHash.add(module);
			}

			string getModuleKey(Module module) {
				if (module.ModuleDefMD.Assembly != null)
					return module.ModuleDefMD.Assembly.ToString();
				return Utils.getBaseName(module.ModuleDefMD.Location);
			}

			public ModuleHash lookup(string assemblyName) {
				ModuleHash moduleHash;
				if (assemblyHash.TryGetValue(assemblyName, out moduleHash))
					return moduleHash;
				return null;
			}
		}

		class ModuleHash {
			ModulesDict modulesDict = new ModulesDict();
			Module mainModule = null;

			public void add(Module module) {
				var asm = module.ModuleDefMD.Assembly;
				if (asm != null && ReferenceEquals(asm.ManifestModule, module.ModuleDefMD)) {
					if (mainModule != null) {
						throw new UserException(string.Format(
							"Two modules in the same assembly are main modules.\n" +
							"Is one 32-bit and the other 64-bit?\n" +
							"  Module1: \"{0}\"" +
							"  Module2: \"{1}\"",
							module.ModuleDefMD.Location,
							mainModule.ModuleDefMD.Location));
					}
					mainModule = module;
				}

				modulesDict.add(module);
			}

			public Module lookup(string moduleName) {
				return modulesDict.lookup(moduleName);
			}

			public IEnumerable<Module> Modules {
				get { return modulesDict.Modules; }
			}
		}

		class ModulesDict {
			IDictionary<string, Module> modulesDict = new Dictionary<string, Module>(StringComparer.Ordinal);

			public void add(Module module) {
				var moduleName = module.ModuleDefMD.Name.String;
				if (lookup(moduleName) != null)
					throw new ApplicationException(string.Format("Module \"{0}\" was found twice", moduleName));
				modulesDict[moduleName] = module;
			}

			public Module lookup(string moduleName) {
				Module module;
				if (modulesDict.TryGetValue(moduleName, out module))
					return module;
				return null;
			}

			public IEnumerable<Module> Modules {
				get { return modulesDict.Values; }
			}
		}

		public bool Empty {
			get { return modules.Count == 0; }
		}

		public Modules(IDeobfuscatorContext deobfuscatorContext) {
			this.deobfuscatorContext = deobfuscatorContext;
		}

		public void add(Module module) {
			if (initializeCalled)
				throw new ApplicationException("initialize() has been called");
			Module otherModule;
			if (modulesDict.TryGetValue(module.ModuleDefMD, out otherModule))
				return;
			modulesDict[module.ModuleDefMD] = module;
			modules.Add(module);
			assemblyHash.add(module);
		}

		public void initialize() {
			initializeCalled = true;
			findAllMemberReferences();
			initAllTypes();
			resolveAllRefs();
		}

		void findAllMemberReferences() {
			Log.v("Finding all MemberReferences");
			int index = 0;
			foreach (var module in modules) {
				if (modules.Count > 1)
					Log.v("Finding all MemberReferences ({0})", module.Filename);
				Log.indent();
				module.findAllMemberReferences(ref index);
				Log.deIndent();
			}
		}

		void resolveAllRefs() {
			Log.v("Resolving references");
			foreach (var module in modules) {
				if (modules.Count > 1)
					Log.v("Resolving references ({0})", module.Filename);
				Log.indent();
				module.resolveAllRefs(this);
				Log.deIndent();
			}
		}

		void initAllTypes() {
			foreach (var module in modules)
				allTypes.AddRange(module.getAllTypes());

			var typeToTypeDef = new Dictionary<TypeDef, MTypeDef>(allTypes.Count);
			foreach (var typeDef in allTypes)
				typeToTypeDef[typeDef.TypeDef] = typeDef;

			// Initialize Owner
			foreach (var typeDef in allTypes) {
				if (typeDef.TypeDef.DeclaringType != null)
					typeDef.Owner = typeToTypeDef[typeDef.TypeDef.DeclaringType];
			}

			// Initialize baseType and derivedTypes
			foreach (var typeDef in allTypes) {
				var baseType = typeDef.TypeDef.BaseType;
				if (baseType == null)
					continue;
				var baseTypeDef = resolveType(baseType) ?? resolveOther(baseType);
				if (baseTypeDef != null) {
					typeDef.addBaseType(baseTypeDef, baseType);
					baseTypeDef.derivedTypes.Add(typeDef);
				}
			}

			// Initialize interfaces
			foreach (var typeDef in allTypes) {
				foreach (var iface in typeDef.TypeDef.InterfaceImpls) {
					var ifaceTypeDef = resolveType(iface.Interface) ?? resolveOther(iface.Interface);
					if (ifaceTypeDef != null)
						typeDef.addInterface(ifaceTypeDef, iface.Interface);
				}
			}

			// Find all non-nested types
			var allTypesDict = new Dictionary<MTypeDef, bool>();
			foreach (var t in allTypes)
				allTypesDict[t] = true;
			foreach (var t in allTypes) {
				foreach (var t2 in t.NestedTypes)
					allTypesDict.Remove(t2);
			}
			nonNestedTypes = new List<MTypeDef>(allTypesDict.Keys);

			foreach (var typeDef in allTypes) {
				if (typeDef.baseType == null || !typeDef.baseType.typeDef.HasModule)
					baseTypes.Add(typeDef);
			}
		}

		class AssemblyKeyDictionary<T> where T : class {
			Dictionary<ITypeDefOrRef, T> dict = new Dictionary<ITypeDefOrRef, T>(new TypeEqualityComparer(SigComparerOptions.CompareAssemblyVersion));
			Dictionary<ITypeDefOrRef, List<ITypeDefOrRef>> refs = new Dictionary<ITypeDefOrRef, List<ITypeDefOrRef>>(TypeEqualityComparer.Instance);

			public T this[ITypeDefOrRef type] {
				get {
					T value;
					if (tryGetValue(type, out value))
						return value;
					throw new KeyNotFoundException();
				}
				set {
					dict[type] = value;

					if (value != null) {
						List<ITypeDefOrRef> list;
						if (!refs.TryGetValue(type, out list))
							refs[type] = list = new List<ITypeDefOrRef>();
						list.Add(type);
					}
				}
			}

			public bool tryGetValue(ITypeDefOrRef type, out T value) {
				return dict.TryGetValue(type, out value);
			}

			public bool tryGetSimilarValue(ITypeDefOrRef type, out T value) {
				List<ITypeDefOrRef> list;
				if (!refs.TryGetValue(type, out list)) {
					value = default(T);
					return false;
				}

				// Find a type whose version is >= type's version and closest to it.

				ITypeDefOrRef foundType = null;
				var typeAsmName = type.DefinitionAssembly;
				IAssembly foundAsmName = null;
				foreach (var otherRef in list) {
					if (!dict.TryGetValue(otherRef, out value))
						continue;

					if (typeAsmName == null) {
						foundType = otherRef;
						break;
					}

					var otherAsmName = otherRef.DefinitionAssembly;
					if (otherAsmName == null)
						continue;
					// Check pkt or we could return a type in eg. a SL assembly when it's not a SL app.
					if (!PublicKeyBase.TokenEquals(typeAsmName.PublicKeyOrToken, otherAsmName.PublicKeyOrToken))
						continue;
					if (typeAsmName.Version > otherAsmName.Version)
						continue;	// old version

					if (foundType == null) {
						foundAsmName = otherAsmName;
						foundType = otherRef;
						continue;
					}

					if (foundAsmName.Version <= otherAsmName.Version)
						continue;
					foundAsmName = otherAsmName;
					foundType = otherRef;
				}

				if (foundType != null) {
					value = dict[foundType];
					return true;
				}

				value = default(T);
				return false;
			}
		}

		AssemblyKeyDictionary<MTypeDef> typeToTypeDefDict = new AssemblyKeyDictionary<MTypeDef>();
		public MTypeDef resolveOther(ITypeDefOrRef type) {
			if (type == null)
				return null;
			type = type.ScopeType;
			if (type == null)
				return null;

			MTypeDef typeDef;
			if (typeToTypeDefDict.tryGetValue(type, out typeDef))
				return typeDef;

			var typeDefinition = deobfuscatorContext.resolveType(type);
			if (typeDefinition == null) {
				typeToTypeDefDict.tryGetSimilarValue(type, out typeDef);
				typeToTypeDefDict[type] = typeDef;
				return typeDef;
			}

			if (typeToTypeDefDict.tryGetValue(typeDefinition, out typeDef)) {
				typeToTypeDefDict[type] = typeDef;
				return typeDef;
			}

			typeToTypeDefDict[type] = null;	// In case of a circular reference
			typeToTypeDefDict[typeDefinition] = null;

			typeDef = new MTypeDef(typeDefinition, null, 0);
			typeDef.addMembers();
			foreach (var iface in typeDef.TypeDef.InterfaceImpls) {
				var ifaceDef = resolveOther(iface.Interface);
				if (ifaceDef == null)
					continue;
				typeDef.addInterface(ifaceDef, iface.Interface);
			}
			var baseDef = resolveOther(typeDef.TypeDef.BaseType);
			if (baseDef != null)
				typeDef.addBaseType(baseDef, typeDef.TypeDef.BaseType);

			typeToTypeDefDict[type] = typeDef;
			if (type != typeDefinition)
				typeToTypeDefDict[typeDefinition] = typeDef;
			return typeDef;
		}

		public MethodNameGroups initializeVirtualMembers() {
			var groups = new MethodNameGroups();
			foreach (var typeDef in allTypes)
				typeDef.initializeVirtualMembers(groups, this);
			return groups;
		}

		public void onTypesRenamed() {
			foreach (var module in modules)
				module.onTypesRenamed();
		}

		public void cleanUp() {
#if PORT
			foreach (var module in DotNetUtils.typeCaches.invalidateAll())
				AssemblyResolver.Instance.removeModule(module);
#endif
		}

		// Returns null if it's a non-loaded module/assembly
		IEnumerable<Module> findModules(ITypeDefOrRef type) {
			var scope = type.Scope;
			if (scope == null)
				return null;

			if (scope.ScopeType == ScopeType.AssemblyRef)
				return findModules((AssemblyRef)scope);

			if (scope.ScopeType == ScopeType.ModuleDef) {
				var modules = findModules((ModuleDef)scope);
				if (modules != null)
					return modules;
			}

			if (scope.ScopeType == ScopeType.ModuleRef) {
				var moduleRef = (ModuleRef)scope;
				if (moduleRef.Name == type.OwnerModule.Name) {
					var modules = findModules(type.OwnerModule);
					if (modules != null)
						return modules;
				}

				var asm = type.OwnerModule.Assembly;
				if (asm == null)
					return null;
				var moduleHash = assemblyHash.lookup(asm.FullName);
				if (moduleHash == null)
					return null;
				var module = moduleHash.lookup(moduleRef.Name.String);
				if (module == null)
					return null;
				return new List<Module> { module };
			}

			throw new ApplicationException(string.Format("scope is an unsupported type: {0}", scope.GetType()));
		}

		IEnumerable<Module> findModules(AssemblyRef assemblyRef) {
			var moduleHash = assemblyHash.lookup(assemblyRef.FullName);
			if (moduleHash != null)
				return moduleHash.Modules;
			return null;
		}

		IEnumerable<Module> findModules(ModuleDef moduleDef) {
			Module module;
			if (modulesDict.TryGetValue(moduleDef, out module))
				return new List<Module> { module };
			return null;
		}

		bool isAutoCreatedType(ITypeDefOrRef typeRef) {
			var ts = typeRef as TypeSpec;
			if (ts == null)
				return false;
			var sig = ts.TypeSig;
			if (sig == null)
				return false;
			return sig.IsSZArray || sig.IsArray || sig.IsPointer;
		}

		public MTypeDef resolveType(ITypeDefOrRef typeRef) {
			var modules = findModules(typeRef);
			if (modules == null)
				return null;
			foreach (var module in modules) {
				var rv = module.resolveType(typeRef);
				if (rv != null)
					return rv;
			}
			if (isAutoCreatedType(typeRef))
				return null;
			Log.e("Could not resolve TypeReference {0} ({1:X8}) (from {2} -> {3})",
						Utils.removeNewlines(typeRef),
						typeRef.MDToken.ToInt32(),
						typeRef.OwnerModule,
						typeRef.Scope);
			return null;
		}

		public MMethodDef resolveMethod(MemberRef methodRef) {
			if (methodRef.DeclaringType == null)
				return null;
			var modules = findModules(methodRef.DeclaringType);
			if (modules == null)
				return null;
			foreach (var module in modules) {
				var rv = module.resolveMethod(methodRef);
				if (rv != null)
					return rv;
			}
			if (isAutoCreatedType(methodRef.DeclaringType))
				return null;
			Log.e("Could not resolve MethodReference {0} ({1:X8}) (from {2} -> {3})",
						Utils.removeNewlines(methodRef),
						methodRef.MDToken.ToInt32(),
						methodRef.DeclaringType.OwnerModule,
						methodRef.DeclaringType.Scope);
			return null;
		}

		public MFieldDef resolveField(MemberRef fieldReference) {
			if (fieldReference.DeclaringType == null)
				return null;
			var modules = findModules(fieldReference.DeclaringType);
			if (modules == null)
				return null;
			foreach (var module in modules) {
				var rv = module.resolveField(fieldReference);
				if (rv != null)
					return rv;
			}
			if (isAutoCreatedType(fieldReference.DeclaringType))
				return null;
			Log.e("Could not resolve FieldReference {0} ({1:X8}) (from {2} -> {3})",
						Utils.removeNewlines(fieldReference),
						fieldReference.MDToken.ToInt32(),
						fieldReference.DeclaringType.OwnerModule,
						fieldReference.DeclaringType.Scope);
			return null;
		}
	}
}
