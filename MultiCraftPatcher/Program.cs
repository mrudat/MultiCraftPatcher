using Mutagen.Bethesda;
using Mutagen.Bethesda.FormKeys.SkyrimLE;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;
using Noggog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MultiCraftPatcher
{
    public class Program
    {
        private readonly IPatcherState<ISkyrimMod, ISkyrimModGetter> state;

        public Program(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            this.state = state;
        }

        public static async Task<int> Main(string[] args)
        {
            return await SynthesisPipeline.Instance
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
                .SetTypicalOpen(GameRelease.SkyrimLE, "MultiCraftPatch.esp")
                .Run(args);
        }

        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            new Program(state).RunPatch();
        }

        private static readonly IFormLinkGetter<IMiscItemGetter> IronIngot = Skyrim.MiscItem.IngotIron;

        private static readonly IFormLinkGetter<IMiscItemGetter> Firewood = Skyrim.MiscItem.Firewood01;

        private static readonly ModKey CCOR = ModKey.FromNameAndExtension("Complete Crafting Overhaul_Remade.esp");

        private static readonly List<int> chestSizes = new()
        {
            10,
            100,
            1000
        };

        private static readonly HashSet<IFormLinkGetter<IKeywordGetter>> ForbiddenWorkbenchKeywords = new()
        {
            Skyrim.Keyword.CraftingSmithingSharpeningWheel,
            Skyrim.Keyword.CraftingSmithingArmorTable
        };

        private static readonly MiscItem.TranslationMask templateChestCopyMask = new(true)
        {
            EditorID = false,
            Name = false,
        };

        private void RunPatch()
        {
            // TODO CompleteCraftingOverhaul_Remade.Keyword.CraftingDisabledRecipe_CCO
            if (state.LinkCache.TryResolve<IKeywordGetter>("CraftingDisabledRecipe_CCO", out var kywd))
                ForbiddenWorkbenchKeywords.Add(kywd.AsLinkGetter());

            Dictionary<IFormLinkGetter<ISkyrimMajorRecordGetter>, Dictionary<int, IMiscItemGetter>> chestsByItemAndCount = IndexChests();

            var templateChest = chestsByItemAndCount.First().Value.First().Value;

            var ironIngredient = new ContainerEntry();
            ironIngredient.Item.Count = 1;
            ironIngredient.Item.Item.SetTo(IronIngot);

            var firewoodIngredient = new ContainerEntry();
            firewoodIngredient.Item.Count = 2;
            firewoodIngredient.Item.Item.SetTo(Firewood);

            foreach (var recipe in state.LoadOrder.PriorityOrder.ConstructibleObject().WinningOverrides())
            {
                var recipeEditorID = recipe.EditorID;
                if (recipeEditorID is not null)
                {
                    if (recipeEditorID.StartsWith("BYOHHouse")) continue;
                    if (recipeEditorID.Contains("CCO_Learn")) continue;
                    if (recipeEditorID.StartsWith("CCO_Menu")) continue;
                    if (recipeEditorID.StartsWith("CCF_Option")) continue;
                    if (recipeEditorID.EndsWith("0Chest")) continue;
                }

                if (recipe.Items is null) continue;

                var createdObjectLink = recipe.CreatedObject.AsSetter();
                if (createdObjectLink.IsNull) continue;

                if (ForbiddenWorkbenchKeywords.Contains(recipe.WorkbenchKeyword)) continue;


                // TODO extract method and memoize.
                var createdObject = createdObjectLink.TryResolve(state.LinkCache);
                if (createdObject is null) continue;
                var proxtObject = createdObject;

                var createdObjectCount = recipe.CreatedObjectCount;

                var scriptedOverride = false;

                foreach (var script in GetScripts(createdObject))
                {
                    switch (script.Name)
                    {
                        case "CCO_CraftingTorch":
                        case "CCO_SubCraftingArmor":
                            createdObjectCount = (ushort?)script.Properties.OfType<ScriptIntProperty>().Where(x => x.Name == "NumberCreated").SingleOrDefault()?.Data;
                            break;
                        case "CCO_SubCraftingItem":
                            createdObjectCount = (ushort?)script.Properties.OfType<ScriptIntProperty>().Where(x => x.Name == "ItemCount").SingleOrDefault()?.Data;
                            break;
                        default:
                            Console.WriteLine($"Might need to add support for {script.Name}");
                            break;
                    }

                    switch (script.Name)
                    {
                        case "CCO_CraftingTorch":
                            createdObject = script.Properties.OfType<ScriptObjectProperty>().Where(x => x.Name == "Torch").SingleOrDefault()?.Object?.TryResolve<ILightGetter>(state.LinkCache);
                            scriptedOverride = true;
                            break;
                        case "CCO_SubCraftingArmor":
                        case "CCO_SubCraftingItem":
                            createdObject = script.Properties.OfType<ScriptObjectProperty>().Where(x => x.Name == "CraftedObject").SingleOrDefault()?.Object?.TryResolve<IConstructibleGetter>(state.LinkCache);
                            scriptedOverride = true;
                            break;
                        default:
                            break;
                    }
                }

                if (createdObjectCount is null)
                    createdObjectCount = 1;

                if (createdObjectCount == 0) continue;

                if (scriptedOverride)
                {
                    if (createdObject is null) continue;
                    createdObjectLink = createdObject.AsLink();
                }

                if (createdObject is not INamedGetter namedObject) continue;

                var objectName = namedObject.Name;
                if (objectName is null) continue;

                if (createdObject is not IWeightValueGetter weightValueGetter) continue;

                var objectValue = weightValueGetter.Value;
                var objectWeight = weightValueGetter.Weight;

                if (CountUnbalancedBrackets(objectName) != 0)
                {
                    Console.WriteLine($"Skipping {recipe} as {createdObject}'s name contains unbalanced '['s or ']'s");
                    continue;
                }

                var createdObjectEditorID = createdObject.EditorID;
                if (createdObjectEditorID is null) continue;
                if (createdObjectEditorID.EndsWith("0Chest")) continue;

                Console.WriteLine($"Deriving new crafting recipes from {recipe}");


                foreach (var multiplier in chestSizes)
                {
                    // compiler bug, createdObjectCount is known to be not null.
                    var quantity = (int)(createdObjectCount * multiplier);
                    var miscEDID = $"{createdObjectEditorID}x${quantity}Chest";
                    var bigRecipeEditorID = $"{recipeEditorID}x${quantity}Chest";

                    var objectChest = chestsByItemAndCount.Autovivify(createdObjectLink).Autovivify(quantity, () =>
                        {
                            var newMiscObject = state.PatchMod.MiscItems.AddNew(miscEDID);
                            newMiscObject.DeepCopyIn(templateChest, new MiscItem.TranslationMask(true)
                            {
                                EditorID = false,
                                Name = false,
                            });

                            newMiscObject.Name = $"{objectName} x${quantity}!";

                            var script = newMiscObject.VirtualMachineAdapter!.Scripts.Where(x => x.Name == "MultiCraftScript").Single();

                            script.Properties.OfType<ScriptIntProperty>().Where(x => x.Name == "AmountToAdd").Single().Data = quantity;
                            script.Properties.OfType<ScriptObjectProperty>().Where(x => x.Name == "ItemToAdd").Single().Object.SetTo(createdObjectLink);
                            script.Properties.OfType<ScriptObjectProperty>().Where(x => x.Name == "Sacrifice").Single().Object.SetTo(newMiscObject);

                            // craftingXP = 3 x itemValue^0.65 + 25
                            // craftingXP - 25 = 3 x itemValue^0.65
                            // (craftingXP - 25) / 3 = itemValue^0.65
                            // itemValue = (craftingXP - 25) / 3)^(1/0.65)

                            // TODO: limit to best available XP, rather than per-object XP?
                            var craftingXP = ((3 * Math.Pow(objectValue, 0.65)) + 25) * quantity;
                            var totalValue = Math.Ceiling(Math.Pow((craftingXP - 25) / 3, 1 / 0.65));

                            newMiscObject.Value = (uint)totalValue;
                            newMiscObject.Weight = (objectWeight * quantity) + 10;

                            return newMiscObject;
                        });

                    if (!state.LinkCache.TryResolve<IConstructibleObjectGetter>(bigRecipeEditorID, out var bigRecipe))
                    {
                        // TODO try harder to find the original recipe.
                        var newBigRecipe = state.PatchMod.ConstructibleObjects.AddNew(bigRecipeEditorID);
                        newBigRecipe.DeepCopyIn(recipe, new(true)
                        {
                            EditorID = false
                        });

                        bool hasIron = false;
                        bool hasFirewood = false;

                        var items = newBigRecipe.Items!;

                        Dictionary<IFormLinkGetter<IItemGetter>, int> ingredients = new();

                        foreach (var item in items)
                        {
                            var ingredient = item.Item.Item;
                            var newQuantity = item.Item.Count * multiplier;
                            if (ingredient.Equals(Firewood))
                            {
                                hasFirewood = true;
                                newQuantity += 2;
                            } else if (ingredient.Equals(IronIngot))
                            {
                                hasIron = true;
                                newQuantity++;
                            }
                            item.Item.Count = newQuantity;
                            ingredients[ingredient] = newQuantity;
                        }

                        if (!hasIron)
                        {
                            items.Add(ironIngredient);
                            ingredients[IronIngot] = 1;
                        }

                        if (!hasFirewood)
                        {
                            items.Add(firewoodIngredient);
                            ingredients[Firewood] = 2;
                        }

                        var conditions = newBigRecipe.Conditions;

                        foreach (var c in conditions)
                        {
                            if (c is not ConditionFloat condition) continue;
                            if (condition.Data is not FunctionConditionData funcData) continue;
                            if (funcData.Function != Condition.Function.GetItemCount) continue;
                            if (funcData.RunOnType != Condition.RunOnType.Subject) continue;
                            var ingredient = funcData.ParameterOneRecord.Cast<IItemGetter>();
                            if (!ingredients.TryGetValue(ingredient, out var newQuantity)) continue;
                            condition.ComparisonValue = newQuantity;
                            ingredients.Remove(ingredient);
                        }

                        foreach (var item in ingredients)
                        {
                            conditions.Add(new ConditionFloat()
                            {
                                CompareOperator = CompareOperator.EqualTo,
                                ComparisonValue = item.Value,
                                Data = new FunctionConditionData()
                                {
                                    ParameterOneRecord = item.Key.AsSetter(),
                                    Function = Condition.Function.GetItemCount,
                                    RunOnType = Condition.RunOnType.Subject
                                }
                            });
                        }

                        newBigRecipe.CreatedObject.SetTo(objectChest);
                        newBigRecipe.CreatedObjectCount = null;
                    }
                }
            }
        }

        private static int CountUnbalancedBrackets(string objectName) => objectName.Select(c => c switch { '[' => 1, ']' => -1, _ => 0, }).Sum();

        private static IReadOnlyList<IScriptEntryGetter> GetScripts(IConstructibleGetter createdObject) => GetVirtualMachineAdapter(createdObject)?.Scripts ?? Array.Empty<IScriptEntryGetter>();

        private static IVirtualMachineAdapterGetter? GetVirtualMachineAdapter(IConstructibleGetter createdObject) => createdObject switch
        {
            IAlchemicalApparatusGetter foo => foo.VirtualMachineAdapter,
            IArmorGetter foo => foo.VirtualMachineAdapter,
            IBookGetter foo => foo.VirtualMachineAdapter,
            IIngredientGetter foo => foo.VirtualMachineAdapter,
            IKeyGetter foo => foo.VirtualMachineAdapter,
            ILightGetter foo => foo.VirtualMachineAdapter,
            IMiscItemGetter foo => foo.VirtualMachineAdapter,
            IWeaponGetter foo => foo.VirtualMachineAdapter,
            IAmmunitionGetter or IIngestibleGetter or IScrollGetter or ISoulGemGetter => null,
            _ => null,
        };

        private Dictionary<IFormLinkGetter<ISkyrimMajorRecordGetter>, Dictionary<int, IMiscItemGetter>> IndexChests()
        {
            Dictionary<IFormLinkGetter<ISkyrimMajorRecordGetter>, Dictionary<int, IMiscItemGetter>> chestsByItemAndCount = new();

            foreach (var chest in state.LoadOrder.PriorityOrder.MiscItem().WinningOverrides())
            {
                var VirtualMachineAdapter = chest.VirtualMachineAdapter;
                if (VirtualMachineAdapter is null) continue;

                var script = VirtualMachineAdapter.Scripts.Where(x => x.Name == "MultiCraftScript").SingleOrDefault();
                if (script is null) continue;

                var itemProp = script.Properties.OfType<ScriptObjectProperty>().Where(x => x.Name == "ItemToAdd").SingleOrDefault();
                if (itemProp is null)
                {
                    Console.WriteLine($"Skipping {chest} because ItemToAdd is not present");
                    continue;
                }

                var countProp = script.Properties.OfType<ScriptIntProperty>().Where(x => x.Name == "AmountToAdd").SingleOrDefault();
                if (countProp is null)
                {
                    Console.WriteLine($"Skipping {chest} because AmounToAdd is not present");
                    continue;
                }

                var item = itemProp.Object;
                var count = countProp.Data;

                chestsByItemAndCount.Autovivify(item)[count] = chest;
            }

            return chestsByItemAndCount;
        }
    }
}
