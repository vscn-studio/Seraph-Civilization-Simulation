using System.Net;
using System.Security.Cryptography;
using System.Text;
using AngleSharp.Dom;
using Cairo;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace SeraphCivilizationSimulation;

internal static class AnnouncementRecipes
{
    // Persist content identity as well as a fast index. Recipe order changes when mods change.
    internal static string Signature(ICoreClientAPI api, GridRecipe recipe)
    {
        string[] Tags(TagSet tags) => tags.IsEmpty ? [] : api.CollectibleTagRegistry.SlowEnumerateTagNames(tags).Order(StringComparer.Ordinal).ToArray();
        object? Ingredient(CraftingRecipeIngredient? i) => i == null ? null : new {
            i.Type, Code=i.Code?.ToString(), i.Name, i.Id, i.Quantity, i.IsTool, i.ToolDurabilityCost,
            i.MatchingType, i.Consume, i.DurabilityChange, i.Break,
            Attributes=i.Attributes?.ToString(), ResolvedAttributes=i.ResolvedAttributes?.ToJsonToken(),
            i.AllowedVariants, i.SkipVariants,
            Tags=new { i.Tags.isDisjunctive, Conditions=i.Tags.conditions?.Select(c=>new { Required=Tags(c.RequiredTags), Forbidden=Tags(c.ForbiddenTags) }).ToArray() },
            ReturnedStack=i.ReturnedStack == null ? null : new { i.ReturnedStack.Type, Code=i.ReturnedStack.Code?.ToString(), i.ReturnedStack.StackSize, Attributes=i.ReturnedStack.Attributes?.ToString() },
            RecipeAttributes=i.RecipeAttributes?.ToString()
        };
        var data = new { Name=recipe.Name?.ToString(), recipe.Width, recipe.Height, recipe.Shapeless,
            recipe.RecipeGroup, recipe.RequiresTrait, recipe.CopyAttributesFrom, recipe.MergeAttributesFrom,
            recipe.AverageDurability, Attributes=recipe.Attributes?.ToString(), recipe.AllowedVariants, recipe.SkipVariants, Output=Ingredient(recipe.Output),
            Inputs=recipe.ResolvedIngredients?.Select(Ingredient).ToArray() };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(data))));
    }
    internal static string Html(ICoreClientAPI api, GridRecipe recipe, int index)
        => "<figure data-game-recipe=\"" + Signature(api,recipe) + "\" data-recipe-index=\"" + index
            + "\"><figcaption>" + WebUtility.HtmlEncode(recipe.Output?.ResolvedItemStack?.GetName() ?? "配方") + "</figcaption></figure>";

    internal static GridRecipe? Resolve(ICoreClientAPI api, IElement element)
    {
        var recipes=api.World.GridRecipes;
        string? signature=element.GetAttribute("data-game-recipe");
        if (recipes == null || signature?.Length != 64) return null;
        if (int.TryParse(element.GetAttribute("data-recipe-index"),out int index)
            && index >= 0 && index < recipes.Count && Signature(api,recipes[index]) == signature) return recipes[index];
        return recipes.FirstOrDefault(recipe => Signature(api,recipe) == signature);
    }
    internal static RichTextComponentBase Component(ICoreClientAPI api, IElement element, AnnouncementInteraction interaction)
    {
        var recipe=Resolve(api,element);
        SlideshowGridRecipeTextComponent? grid=null;
        if (recipe?.Output?.ResolvedItemStack is { } output)
        {
            try
            {
                void Open(ItemStack stack) => AnnouncementGameResources.Open(api,AnnouncementGameResources.HandbookTarget(api,stack));
                grid=new(api,[recipe],28,EnumFloat.Left,Open);
                var stack=output.Clone();
                var result=new RecipeOutput(api,stack,grid,Open);
                var title=AnnouncementTextLayout.Components(api,AnnouncementDom.Markup("<p>" + WebUtility.HtmlEncode(output.GetName())
                    + (recipe.Shapeless ? "（无序配方）" : "") + "</p>"),interaction);
                return new AnnouncementBox(api,title.Append(new RecipeRow(api,grid,result)).ToArray());
            }
            catch (ArgumentException) { grid?.Dispose(); }
        }
        return new AnnouncementBox(api,AnnouncementTextLayout.Components(api,AnnouncementDom.Markup("<p>[配方不可用] "
            + WebUtility.HtmlEncode(element.TextContent) + "</p>"),interaction));
    }
    // Recipe geometry is independent of text baselines and wrapping. Only the native
    // ingredient/output icons navigate to handbook pages; captions remain plain text.
    private sealed class RecipeRow(ICoreClientAPI api, SlideshowGridRecipeTextComponent grid, RecipeOutput output)
        : AnnouncementBox(api,[grid,output])
    {
        internal double ArrowX { get; private set; }
        internal double ArrowY { get; private set; }
        protected override double MeasureContent(double width)
        {
            TextFlowPath[] path = [new(0,0,Math.Max(width,GuiElement.scaled(180)),double.MaxValue)];
            grid.CalcBounds(path,0,0,0,out _);
            output.CalcBounds(path,0,0,0,out _);
            var inputBounds=grid.BoundsPerLine[0];
            var outputBounds=output.BoundsPerLine[0];
            double gridSize=GuiElement.scaled(90), gap=GuiElement.scaled(12), arrowWidth=GuiElement.scaled(20);
            inputBounds.X=inputBounds.Y=0;
            inputBounds.Width=gridSize;
            if (width >= gridSize+2*gap+arrowWidth+outputBounds.Width)
            {
                ArrowX=gridSize+gap;
                ArrowY=gridSize/2;
                outputBounds.X=ArrowX+arrowWidth+gap;
                outputBounds.Y=(gridSize-outputBounds.Height)/2;
                return Math.Max(inputBounds.Height,gridSize);
            }
            // Very narrow containers put the arrow and result together below the grid.
            ArrowX=0;
            outputBounds.X=arrowWidth+gap;
            outputBounds.Y=inputBounds.Height+gap;
            ArrowY=outputBounds.Y+outputBounds.Height/2;
            return outputBounds.Y+outputBounds.Height;
        }
        public override void ComposeElements(Context ctx,ImageSurface surface)
        {
            base.ComposeElements(ctx,surface);
            var bounds=BoundsPerLine[0];
            double x=bounds.X+ContentX+ArrowX, y=bounds.Y+ContentY+ArrowY;
            double w=GuiElement.scaled(20), h=GuiElement.scaled(6);
            ctx.Save(); ctx.SetSourceRGBA(GuiStyle.DialogDefaultTextColor); ctx.LineWidth=GuiElement.scaled(2);
            ctx.MoveTo(x,y); ctx.LineTo(x+w,y);
            ctx.MoveTo(x+w-h,y-h); ctx.LineTo(x+w,y); ctx.LineTo(x+w-h,y+h);
            ctx.Stroke(); ctx.Restore();
        }
    }
    private sealed class RecipeOutput : ItemstackTextComponent
    {
        private readonly ItemStack stack;
        private readonly SlideshowGridRecipeTextComponent grid;
        internal RecipeOutput(ICoreClientAPI api, ItemStack stack, SlideshowGridRecipeTextComponent grid, Action<ItemStack> open)
            : base(api,stack,28,onStackClicked:open) { this.stack=stack; this.grid=grid; }
        public override void ComposeElements(Context ctx,ImageSurface surface)
        {
            var bounds=BoundsPerLine[0];
            ctx.Save(); ctx.SetSourceRGBA(1,1,1,.2);
            ctx.Rectangle(bounds.X,bounds.Y,bounds.Width,bounds.Height); ctx.Fill(); ctx.Restore();
        }
        public override void RenderInteractiveElements(float dt,double x,double y,double z)
        {
            // Grid rendering picks the current wildcard variants. Derive output from those inputs.
            if (grid.GenerateCurrentVisibleOutputStack() is { } generated) stack.SetFrom(generated);
            ShowStacksize=true;
            base.RenderInteractiveElements(dt,x,y,z);
        }
    }
}
