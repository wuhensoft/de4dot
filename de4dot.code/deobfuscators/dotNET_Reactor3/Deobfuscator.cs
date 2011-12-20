﻿/*
    Copyright (C) 2011 de4dot@gmail.com

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
using System.IO;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.MyStuff;
using de4dot.blocks;
using de4dot.code.PE;

namespace de4dot.code.deobfuscators.dotNET_Reactor3 {
	public class DeobfuscatorInfo : DeobfuscatorInfoBase {
		public const string THE_NAME = ".NET Reactor";
		public const string THE_TYPE = "dr3";
		const string DEFAULT_REGEX = DeobfuscatorBase.DEFAULT_VALID_NAME_REGEX;
		BoolOption restoreTypes;
		BoolOption inlineMethods;
		BoolOption removeInlinedMethods;
		BoolOption removeNamespaces;
		BoolOption removeAntiStrongName;

		public DeobfuscatorInfo()
			: base(DEFAULT_REGEX) {
			restoreTypes = new BoolOption(null, makeArgName("types"), "Restore types (object -> real type)", true);
			inlineMethods = new BoolOption(null, makeArgName("inline"), "Inline short methods", true);
			removeInlinedMethods = new BoolOption(null, makeArgName("remove-inlined"), "Remove inlined methods", true);
			removeNamespaces = new BoolOption(null, makeArgName("ns1"), "Clear namespace if there's only one class in it", true);
			removeAntiStrongName = new BoolOption(null, makeArgName("sn"), "Remove anti strong name code", true);
		}

		public override string Name {
			get { return THE_NAME; }
		}

		public override string Type {
			get { return THE_TYPE; }
		}

		public override IDeobfuscator createDeobfuscator() {
			return new Deobfuscator(new Deobfuscator.Options {
				ValidNameRegex = validNameRegex.get(),
				RestoreTypes = restoreTypes.get(),
				InlineMethods = inlineMethods.get(),
				RemoveInlinedMethods = removeInlinedMethods.get(),
				RemoveNamespaces = removeNamespaces.get(),
				RemoveAntiStrongName = removeAntiStrongName.get(),
			});
		}

		protected override IEnumerable<Option> getOptionsInternal() {
			return new List<Option>() {
				restoreTypes,
				inlineMethods,
				removeInlinedMethods,
				removeNamespaces,
				removeAntiStrongName,
			};
		}
	}

	class Deobfuscator : DeobfuscatorBase {
		Options options;
		string obfuscatorName = DeobfuscatorInfo.THE_NAME;

		DecrypterType decrypterType;
		NativeLibSaver nativeLibSaver;
		List<UnpackedFile> unpackedFiles = new List<UnpackedFile>();

		bool unpackedNativeFile = false;
		bool canRemoveDecrypterType = true;
		bool startedDeobfuscating = false;

		internal class Options : OptionsBase {
			public bool RestoreTypes { get; set; }
			public bool InlineMethods { get; set; }
			public bool RemoveInlinedMethods { get; set; }
			public bool RemoveNamespaces { get; set; }
			public bool RemoveAntiStrongName { get; set; }
		}

		public override string Type {
			get { return DeobfuscatorInfo.THE_TYPE; }
		}

		public override string TypeLong {
			get { return DeobfuscatorInfo.THE_NAME + " 3.x"; }
		}

		public override string Name {
			get { return obfuscatorName; }
		}

		public override bool CanInlineMethods {
			get { return startedDeobfuscating ? options.InlineMethods : true; }
		}

		public Deobfuscator(Options options)
			: base(options) {
			this.options = options;

			if (options.RemoveNamespaces)
				this.RenamingOptions |= RenamingOptions.RemoveNamespaceIfOneType;
			else
				this.RenamingOptions &= ~RenamingOptions.RemoveNamespaceIfOneType;
		}

		public override byte[] unpackNativeFile(PeImage peImage) {
			var unpackerv3 = new ApplicationModeUnpacker(peImage);
			var data = unpackerv3.unpack();
			if (data == null)
				return null;

			unpackedFiles.AddRange(unpackerv3.EmbeddedAssemblies);
			unpackedNativeFile = true;
			ModuleBytes = data;
			return data;
		}

		public override bool getDecryptedModule(ref byte[] newFileData, ref Dictionary<uint, DumpedMethod> dumpedMethods) {
			if (!nativeLibSaver.Detected)
				return false;

			var fileData = ModuleBytes ?? DeobUtils.readModule(module);
			var peImage = new PeImage(fileData);
			if (!nativeLibSaver.patch(peImage))
				return false;

			newFileData = fileData;
			return true;
		}

		public override IDeobfuscator moduleReloaded(ModuleDefinition module) {
			var newOne = new Deobfuscator(options);
			newOne.setModule(module);
			newOne.decrypterType = new DecrypterType(module, decrypterType);
			newOne.nativeLibSaver = new NativeLibSaver(module, nativeLibSaver);
			return newOne;
		}

		public override void init(ModuleDefinition module) {
			base.init(module);
		}

		static Regex isRandomName = new Regex(@"^[A-Z]{30,40}$");
		static Regex isRandomNameMembers = new Regex(@"^[a-zA-Z0-9]{9,11}$");	// methods, fields, props, events
		static Regex isRandomNameTypes = new Regex(@"^[a-zA-Z0-9]{18,19}(?:`\d+)?$");	// types, namespaces

		bool checkValidName(string name, Regex regex) {
			if (isRandomName.IsMatch(name))
				return false;
			if (regex.IsMatch(name)) {
				if (RandomNameChecker.isRandom(name))
					return false;
				if (!RandomNameChecker.isNonRandom(name))
					return false;
			}
			return checkValidName(name);
		}

		public override bool isValidNamespaceName(string ns) {
			if (ns == null)
				return false;
			if (ns.Contains("."))
				return base.isValidNamespaceName(ns);
			return checkValidName(ns, isRandomNameTypes);
		}

		public override bool isValidTypeName(string name) {
			return name != null && checkValidName(name, isRandomNameTypes);
		}

		public override bool isValidMethodName(string name) {
			return name != null && checkValidName(name, isRandomNameMembers);
		}

		public override bool isValidPropertyName(string name) {
			return name != null && checkValidName(name, isRandomNameMembers);
		}

		public override bool isValidEventName(string name) {
			return name != null && checkValidName(name, isRandomNameMembers);
		}

		public override bool isValidFieldName(string name) {
			return name != null && checkValidName(name, isRandomNameMembers);
		}

		public override bool isValidGenericParamName(string name) {
			return name != null && checkValidName(name, isRandomNameMembers);
		}

		public override bool isValidMethodArgName(string name) {
			return name != null && checkValidName(name, isRandomNameMembers);
		}

		protected override int detectInternal() {
			int val = 0;

			if (unpackedNativeFile)
				val += 100;

			int sum = convert(unpackedNativeFile) +
					convert(decrypterType.Detected) +
					convert(nativeLibSaver.Detected);
			if (sum > 0)
				val += 100 + 10 * (sum - 1);

			return val;
		}

		static int convert(bool b) {
			return b ? 1 : 0;
		}

		protected override void scanForObfuscator() {
			decrypterType = new DecrypterType(module);
			decrypterType.find();
			nativeLibSaver = new NativeLibSaver(module);
			nativeLibSaver.find();
			obfuscatorName = detectVersion();
			if (unpackedNativeFile)
				obfuscatorName += " (native)";
		}

		string detectVersion() {
			return DeobfuscatorInfo.THE_NAME + " 3.x";
		}

		public override void deobfuscateBegin() {
			base.deobfuscateBegin();

			staticStringDecrypter.add(decrypterType.StringDecrypter1, (method2, args) => {
				return decrypterType.decrypt1((string)args[0]);
			});
			staticStringDecrypter.add(decrypterType.StringDecrypter2, (method2, args) => {
				return decrypterType.decrypt2((string)args[0]);
			});

			if (Operations.DecryptStrings == OpDecryptString.None)
				canRemoveDecrypterType = false;

			addCctorInitCallToBeRemoved(nativeLibSaver.InitMethod);
			addResourceToBeRemoved(nativeLibSaver.Resource, "Native lib resource");
			addTypeToBeRemoved(nativeLibSaver.Type, "Native lib saver type");

			foreach (var initMethod in decrypterType.InitMethods)
				addCctorInitCallToBeRemoved(initMethod);

			dumpUnpackedFiles();

			startedDeobfuscating = true;
		}

		void dumpUnpackedFiles() {
			foreach (var unpackedFile in unpackedFiles)
				DeobfuscatedFile.createAssemblyFile(unpackedFile.data, Path.GetFileNameWithoutExtension(unpackedFile.filename), Path.GetExtension(unpackedFile.filename));
		}

		public override void deobfuscateEnd() {
			removeInlinedMethods();
			if (options.RestoreTypes)
				new TypesRestorer(module).deobfuscate();

			if (canRemoveDecrypterType && !isTypeCalled(decrypterType.Type)) {
				addTypeToBeRemoved(decrypterType.Type, "Decrypter type");
				addModuleReferencesToBeRemoved(decrypterType.ModuleReferences, "Native lib module references");
			}

			base.deobfuscateEnd();
		}

		void removeInlinedMethods() {
			if (!options.InlineMethods || !options.RemoveInlinedMethods)
				return;
			findAndRemoveInlinedMethods();
		}

		public override IEnumerable<string> getStringDecrypterMethods() {
			var list = new List<string>();
			foreach (var method in decrypterType.StringDecrypters)
				list.Add(method.MetadataToken.ToInt32().ToString("X8"));
			return list;
		}
	}
}
