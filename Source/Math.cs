﻿using RimWorld;
using HarmonyLib;
using System.Reflection;
using System.Collections.Generic;
using Verse;
using UnityEngine;
using NCalc;
using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace CrunchyDuck.Math {
	// TODO: Show decimal values in Currently Have, Repeat and Unpause At, but round the ultimate value.
	// TODO: Method to "resolve" a calculation, so it doesn't remember what you've typed in. This would be triggered by ctrl + enter
	// TODO: Add math variable name to the i menu of all objects.
	// TODO: Clothing rules/restriction variable.
	// TODO: Update "look everywhere" to ACTUALLY look everywhere, not just in stockpiles.
	// TODO: Copy and link bills together (BWM)
	// TODO: Language support for core variables. For example, changing my regex to support "ninos" instead of "kids"
	// TODO: Saving and loading bills, menu similar to infocard/variable card.
	// TODO: Next/previous buttons on bill details.
	// TODO: add a button for infocard somewhere easier to access than a bill.
	// TODO: Bill menu opens by default on clicking bench.
	// TODO: Maybe allow things like smelting weapons to use Do Until X.
	// TODO: Look at adding Math to Autoseller mod.
	// TODO: Potentially move compatibility patches into their own project(s).

	[StaticConstructorOnStartup]
	class Math {
		public static string version = "1.4.5";

		// Cached variables
		private static Dictionary<Map, CachedMapData> cachedMaps = new Dictionary<Map, CachedMapData>();

		private static Regex variableNames = new Regex(@"(?:""(?:v|variables)\.)(.+?)(?:"")", RegexOptions.Compiled);
		private static Regex parameterNames = new Regex("(?:(\")(.+?)(\"))|([a-zA-Z0-9]+)", RegexOptions.Compiled);
		public static Dictionary<string, ThingDef> searchableThings = new Dictionary<string, ThingDef>();
		public static Dictionary<string, StatDef> searchableStats = new Dictionary<string, StatDef>();
		public static Dictionary<string, (TraitDef traitDef, int index)> searchableTraits = new Dictionary<string, (TraitDef, int)>();

		public static bool rimfactorySupportEnabled = false;
		public static bool compositableLoadoutsSupportEnabled = false;

		static Math() {
			Check3rdPartyMods();
			PerformPatches();

			// I checked, this does run after all defs are loaded :)
			// Code taken from DebugThingPlaceHelper.TryPlaceOptionsForStackCount
			IndexDefs(searchableStats);
			IndexDefs(MathFilters.CategoryFilter.searchableCategories);
			IndexDefs(searchableThings);
			// The trait system is stupid. Why all this degrees nonsense? Just to mark incompatible traits? Needless.
			foreach (TraitDef traitdef in DefDatabase<TraitDef>.AllDefs) {
				int i = -1;
				foreach (TraitDegreeData stupid_degree_nonsense in traitdef.degreeDatas) {
					i++;
					if (stupid_degree_nonsense.label.NullOrEmpty())
						continue;
					searchableTraits[stupid_degree_nonsense.label.ToParameter()] = (traitdef, i);
				}
			}

			// Make counter methods.
			foreach (StatDef stat in searchableStats.Values) {
				string label = stat.label.ToParameter();
				// Thing methods
				Func<Thing, float> t_method = t => t.GetStatValue(stat) * t.stackCount;
				MathFilters.ThingFilter.counterMethods[label] = t_method;
				// Pawn methods
				Func<Pawn, float> p_method = p => p.GetStatValueForPawn(stat, p);
				MathFilters.PawnFilter.counterMethods[label] = p_method;
				// Thingdef methods
				Func<ThingDef, float> td_method = t => t.GetStatValueAbstract(stat);
				MathFilters.ThingDefFilter.counterMethods[label] = td_method;
			}
		}

		/// <summary>
		/// This ignores the package version, because tiny updates don't matter.
		/// </summary>
		public static bool IsNewImportantVersion(string version_to_check) {
			var x = version.Split('.');
			var y = version_to_check.Split('.');
			return x[0] != y[0] || x[1] != y[1];
		}

		public static bool IsNewImportantVersion() {
			return IsNewImportantVersion(MathSettings.settings.lastVersionInfocardChecked);
		}

		private static void IndexDefs<T>(Dictionary<string, T> dict) where T : Def {
			var thing_list = DefDatabase<T>.AllDefs;

			foreach (T def in thing_list) {
				if (def.label == null) {
					continue;
				}
				dict[def.label.ToParameter()] = def;
			}
		}

		private static void Check3rdPartyMods()
		{
			rimfactorySupportEnabled =
				LoadedModManager.RunningModsListForReading.Any(mod => mod.PackageId == "spdskatr.projectrimfactory");

			compositableLoadoutsSupportEnabled =
				LoadedModManager.RunningModsListForReading.Any(mod => mod.PackageId == "wiri.compositableloadouts");

			if (rimfactorySupportEnabled)
				Log.Message("Math: PRF support enabled.");
			
			if (compositableLoadoutsSupportEnabled)
				Log.Message("Math: Compositable Loadouts Support Enabled.");
		}

		private static void PerformPatches() {
			// What can I say, I prefer a manual method of patching.
			var harmony = new Harmony("CrunchyDuck.Math");
			AddPatch(harmony, typeof(Patch_ExposeBillComponent));
			AddPatch(harmony, typeof(DoConfigInterface_Patch));
			AddPatch(harmony, typeof(Bill_Production_Constructor_Patch));
			AddPatch(harmony, typeof(BillDetails_Patch));
			AddPatch(harmony, typeof(CountProducts_Patch));
			AddPatch(harmony, typeof(Bill_Production_DoConfigInterface_Patch));
			AddPatch(harmony, typeof(Patch_Bill_LabelCap));
			AddPatch(harmony, typeof(Patch_Bill_DoInterface));
			AddPatch(harmony, typeof(Patch_BillStack_DoListing));
			AddPatch(harmony, typeof(Patch_BillCopying));

			if (rimfactorySupportEnabled)  {
				AddPatch(harmony, typeof(Patch_RimFactory_RecipeWorkerCounter_CountProducts));
			}
		}

		private static void AddPatch(Harmony harmony, Type type) {
			var prefix = type.GetMethod("Prefix") != null ? new HarmonyMethod(type, "Prefix") : null;
			var postfix = type.GetMethod("Postfix") != null ? new HarmonyMethod(type, "Postfix") : null;
			var trans = type.GetMethod("Transpiler") != null ? new HarmonyMethod(type, "Transpiler") : null;
			harmony.Patch((MethodBase)type.GetMethod("Target").Invoke(null, null), prefix: prefix, postfix: postfix, transpiler: trans);
		}

		public static void ClearCacheMaps() {
			cachedMaps = new Dictionary<Map, CachedMapData>();
		}

		public static bool DoMathForField(string equation, InputField field) {
			float res = 0;
			if (!DoMath(equation, field.bc, ref res))
				return false;
			field.CurrentValue = res;
			return true;
		}

			/// <returns>True if sequence is valid.</returns>
		public static bool DoMath(string equation, BillComponent bc, ref float result) {
			if (equation.NullOrEmpty())
				return false;

			try {
				if (!ParseUserVariables(ref equation))
					return false;
			}
			// TODO: Some way of notifying the user that they performed infinite recursion.
			catch (InfiniteRecursionException) {
				return false;
			}
			List<string> parameter_list = new List<string>();
			foreach (Match match in parameterNames.Matches(equation)) {
				// Matched single word.
				if (match.Groups[4].Success) {
					parameter_list.Add(match.Groups[4].Value);
					continue;
				}

				// Reformat the user input to work for ncalc.
				// The reason I use "pawn" rather than [pawn] is for language compatibility.
				// My spanish friend complained that [ and ] aren't on his keyboard.
				// Spanish people aren't allowed to program.
				int i = match.Index;
				int i2 = match.Index + match.Length - 1;
				equation = equation.Remove(i, 1).Insert(i, "[");
				equation = equation.Remove(i2, 1).Insert(i2, "]");

				string str2 = match.Groups[2].Value;
				parameter_list.Add(str2);
			}
			Expression e = new Expression(equation);
			AddParameters(e, bc, parameter_list);
			// KNOWN BUG: `if` equations don't properly update. This is an ncalc issue - it evaluates the current path and ignores the other.
			if (e.HasErrors())
				return false;
			object ncalc_result;
			try {
				ncalc_result = e.Evaluate();
			}
			// For some reason, HasErrors() doesn't check if parameters are valid.
			catch (ArgumentException) {
				return false;
			}

			Type type = ncalc_result.GetType();
			Type[] accepted_types = new Type[] { typeof(int), typeof(decimal), typeof(double), typeof(float) };
			if (!accepted_types.Contains(type))
				return false;

			try {
				// this is dumb but necessary
				result = (int)Convert.ChangeType(Convert.ChangeType(ncalc_result, type), typeof(int));
			}
			// Divide by 0, mostly.
			catch (OverflowException) {
				result = 999999;
			}
			return true;
		}

		public static CachedMapData GetCachedMap(Bill_Production bp) {
			if (bp == null)
				return null;
			try {
				Map map = bp.Map;
				return GetCachedMap(bp.Map);
			}
			catch (NullReferenceException) {
				return null;
			}
		}

		public static CachedMapData GetCachedMap(Map map) {
			// I was able to get a null error by abandoning a base. This handles that.
			if (map == null)
				return null;
			if (!cachedMaps.ContainsKey(map)) {
				// Generate cache.
				cachedMaps[map] = new CachedMapData(map);
			}
			CachedMapData cache = cachedMaps[map];
			return cache;
		}

		public static bool ParseUserVariables(ref string str, int recursion_level = 0) {
			if (recursion_level >= 5)
				throw new InfiniteRecursionException();
			Match match = variableNames.Match(str, 0);
			while (match.Success) {
				string variable_name = match.Groups[1].Value;
				if (!MathSettings.settings.userVariablesDict.TryGetValue(variable_name, out UserVariable uv)){
					return false;
				}
				string equation = uv.equation;

				// Resolve any references this equation has.
				if (!ParseUserVariables(ref equation, recursion_level + 1))
					return false;

				// Ensures things are parsed in a logical way.
				equation = "(" + equation + ")";
				str = str.Remove(match.Index, match.Length).Insert(match.Index, equation);

				match = variableNames.Match(str, match.Index + equation.Length);
			}
			return true;
		}

		public static void AddParameters(Expression e, BillComponent bc, List<string> parameter_list) {
			CachedMapData cache = bc.Cache;
			if (cache == null) {
				BillManager.instance.RemoveBillComponent(bc);
				return;
			}

			foreach (string parameter in parameter_list) {
				if (cache.SearchVariable(parameter, bc, out float count)) {
					e.Parameters[parameter] = count;
				}
			}
		}
	}

	public class InfiniteRecursionException : Exception {}
}