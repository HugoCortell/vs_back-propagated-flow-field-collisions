using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace CollisionFlowFields
{
	public class ItemFlowFieldAuthoringWand : Item
	{
		const int HighlightId = 934202;
		static readonly Dictionary<string, BlockPos> SourceByUid = new();

		public override void OnHeldAttackStart(ItemSlot slot, EntityAgent byEntity, BlockSelection bs, EntitySelection es, ref EnumHandHandling handling)
		{
			handling = EnumHandHandling.PreventDefault;
			if (bs?.Position == null) return;

			var entityPlayer = byEntity as EntityPlayer;
			var entityPlayerEntity = entityPlayer?.Player;
			if (entityPlayerEntity == null || entityPlayerEntity.WorldData.CurrentGameMode != EnumGameMode.Creative) return;

			var playerUID = entityPlayer.PlayerUID;
			SourceByUid[playerUID] = bs.Position.Copy();

			if (api.Side == EnumAppSide.Client)
			{
				var clientAPI = api as ICoreClientAPI; if (clientAPI == null) return;
				clientAPI.World.HighlightBlocks(clientAPI.World.Player, HighlightId, new List<BlockPos> { SourceByUid[playerUID] });
				clientAPI.ShowChatMessage($"Flowfield source set: {bs.Position.X},{bs.Position.Y},{bs.Position.Z}");
			}
		}

		public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection bs, EntitySelection es, bool firstEvent, ref EnumHandHandling handling)
		{
			handling = EnumHandHandling.PreventDefault;
			if (!firstEvent) return;

			var playerEntity = byEntity as EntityPlayer;
			var playerEntityPlayer = playerEntity?.Player;
			if (playerEntityPlayer == null || playerEntityPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative) return;

			var uid = playerEntity.PlayerUID;
			if (!SourceByUid.TryGetValue(uid, out var source) || source == null)
			{
				if (api.Side == EnumAppSide.Client) (api as ICoreClientAPI)?.ShowChatMessage("No source selected. Left click a block first!");
				return;
			}

			// Print json
			if (api.Side == EnumAppSide.Client)
			{
				var clientAPI = api as ICoreClientAPI; if (clientAPI == null) return;
				var (entries, _) = Solve(clientAPI.World, source);
				var stringBuilder = new StringBuilder();

				var sourceBlock = clientAPI.World.BlockAccessor.GetBlock(source);
				bool perVariant = sourceBlock?.Variant != null && sourceBlock.Variant.Count > 0;
				string variantKey = sourceBlock?.Code?.Path ?? "";
				string indent = perVariant ? "    " : "  ";

				if (perVariant)
				{
					stringBuilder.AppendLine("collisionFlowField: {");
					stringBuilder.Append("  \"").Append(variantKey).AppendLine("\": [");
				}
				else { stringBuilder.AppendLine("collisionFlowField: ["); }

				for (int i = 0; i < entries.Count; i++)
				{
					var entrieslist = entries[i];
					stringBuilder.Append(indent)
						.Append("{ pos: [").Append(entrieslist.dx).Append(", ")
						.Append(entrieslist.dy).Append(", ").Append(entrieslist.dz)
						.Append("], dir: ").Append(entrieslist.dir)
						.Append(", h8: ").Append(entrieslist.h8).Append(" }");
					stringBuilder.AppendLine(i == entries.Count - 1 ? "" : ",");
				}

				if (perVariant)
				{
					stringBuilder.AppendLine("  ]");
					stringBuilder.AppendLine("}");
				}
				else { stringBuilder.AppendLine("]"); }

				stringBuilder.AppendLine($"// Resolved {entries.Count} connected author blocks. Stragglers ignored.");

				clientAPI.Logger.Notification("{0}", stringBuilder.ToString()); // avoid brace-formatting exceptions
				clientAPI.ShowChatMessage("Printed collisionFlowField entries to client log!");
			}
		}

		// Draw preview lines while held | opaque pass
		public override void OnHeldRenderOpaque(ItemSlot inSlot, IClientPlayer byPlayer)
		{
			var clientAPI = api as ICoreClientAPI; if (clientAPI == null) return;
			if (byPlayer?.WorldData?.CurrentGameMode != EnumGameMode.Creative) return;

			if (!SourceByUid.TryGetValue(byPlayer.PlayerUID, out var source) || source == null) return;
			var (_, edges) = Solve(clientAPI.World, source); if (edges.Count == 0) return;

			int col = ColorUtil.ToRgba(255, 255, 0, 255); // bright magenta-ish Z draw above the blocks to prevent depth culling

			foreach (var (from, to) in edges)
			{
				float x1 = (from.X - source.X) + 0.5f;
				float z1 = (from.Z - source.Z) + 0.5f;
				float y1 = (from.Y - source.Y) + 1.05f;

				float x2 = (to.X - source.X) + 0.5f;
				float z2 = (to.Z - source.Z) + 0.5f;
				float y2 = (to.Y - source.Y) + 1.05f;

				clientAPI.Render.RenderLine(source, x1, y1, z1, x2, y2, z2, col);
			}
		}

		static bool IsAuthor(Block block) => block?.Attributes?["flowfieldAuthor"].AsBool(false) == true;

		static int GetH8(Block block)
		{
			int h8 = 8;
			var blockVariant = block.Variant?["h8"];
			if (!string.IsNullOrEmpty(blockVariant) && int.TryParse(blockVariant, out var v)) h8 = GameMath.Clamp(v, 1, 8);
			return h8;
		}

		static (List<(int dx, int dy, int dz, int dir, int h8)> entries, List<(BlockPos from, BlockPos to)> edges) Solve(IWorldAccessor world, BlockPos source)
		{
			source = source.Copy();
			var blockAccessor = world.BlockAccessor;

			var blockQueue = new Queue<BlockPos>();
			var seen = new HashSet<BlockPos>();
			var entries = new List<(int dx, int dy, int dz, int dir, int h8)>();
			var edges = new List<(BlockPos from, BlockPos to)>();

			blockQueue.Enqueue(source);
			seen.Add(source);

			while (blockQueue.Count > 0)
			{
				var current = blockQueue.Dequeue();
				foreach (var face in BlockFacing.ALLFACES)
				{
					var neighbour = current.AddCopy(face);
					if (!seen.Add(neighbour.Copy())) continue;

					var neighbourBlock = blockAccessor.GetBlock(neighbour);
					if (!IsAuthor(neighbourBlock)) continue;

					int h8 = GetH8(neighbourBlock);
					int direction = face.Opposite.Index; // npos points toward cur

					entries.Add((neighbour.X - source.X, neighbour.Y - source.Y, neighbour.Z - source.Z, direction, h8));
					edges.Add((neighbour, current));

					blockQueue.Enqueue(neighbour);
				}
			}

			entries = entries.OrderBy(e => e.dy).ThenBy(e => e.dz).ThenBy(e => e.dx).ToList();
			return (entries, edges);
		}
	}

	public class ModSystemFlowFieldAuthoringWand : ModSystem
	{
		public override void Start(ICoreAPI api) { api.RegisterItemClass("ItemFlowFieldAuthoringWand", typeof(ItemFlowFieldAuthoringWand)); }
	}
}