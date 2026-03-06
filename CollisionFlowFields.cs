using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace CollisionFlowFields
{
	// One "cell", one collider block to place.
	// dir, where this specific collider should step next (towards the source), 0-5 -> BlockFacing.ALLFACES index
	// h8, height in 1-8 (0.125f increments), 1 is 0.125f, 8 is a full sized block
	public readonly struct FlowCell
	{
		public readonly sbyte dx, dy, dz;
		public readonly byte dir, h8;

		public FlowCell(int dx, int dy, int dz, int dirIndex, int h8 = 8)
		{
			this.dx = (sbyte)dx;
			this.dy = (sbyte)dy;
			this.dz = (sbyte)dz;
			this.dir = (byte)GameMath.Clamp(dirIndex, 0, 5);
			this.h8 = (byte)GameMath.Clamp(h8, 1, 8);
		}

		public FlowCell(int dx, int dy, int dz, BlockFacing dir, int h8 = 8) : this(dx, dy, dz, dir?.Index ?? 0, h8) { }
	}

	
	public enum FlowColliderMode : byte
	{
		Solid = 0,
		PassThrough = 1
	}

	public readonly struct FlowDef
	{
		public readonly FlowCell[] Cells;
		public readonly FlowColliderMode Mode;

		public FlowDef(FlowCell[] cells, FlowColliderMode mode)
		{
			Cells = cells;
			Mode = mode;
		}
	}

	// The invisible collider block
	// Expected variants: dir = n/e/s/w/u/d, h8 = 1-8
	public class BlockFlowCollider : Block
	{
		static readonly Cuboidf[][] BoxesByH8 = InitBoxes();

		Cuboidf[] cachedCollisionBoxes = Array.Empty<Cuboidf>();
		Cuboidf[] cachedSelectionBoxes = Array.Empty<Cuboidf>();
		BlockFacing? cachedFlowDir;
		ModSystemCollisionFlowFields? cachedSys;
		internal BlockFacing? CachedFlowDir => cachedFlowDir;

		static Cuboidf[][] InitBoxes()
		{
			var boxArray = new Cuboidf[9][];
			boxArray[0] = Array.Empty<Cuboidf>();
			for (int h = 1; h <= 8; h++)
			{
				float y2 = h / 8f;
				boxArray[h] = new[] { new Cuboidf(0, 0, 0, 1, y2, 1) };
			}
			return boxArray;
		}

		public override void OnLoaded(ICoreAPI api)
		{
			base.OnLoaded(api);

			// Invisible, but don't cull neighbour faces or block light
			DrawType = EnumDrawType.Empty;
			RenderPass = EnumChunkRenderPass.Transparent;

			// Prevent creating a hole in the ground that peers into the void.
			// If SideOpaque is true, neighboring block faces get culled, but since we render nothing, you end up seeing through the world.
			SideOpaque = new SmallBoolArray(0);
			SideAo = new SmallBoolArray(0);
			EmitSideAo = 0;

			// We still collide via GetCollisionBoxes(), but shouldn't count as "solid" for attachments/snow/etc.
			SideSolid = new SmallBoolArray(0);

			// Default in BlockType is 99 - we must not black out areas.
			LightAbsorption = 0;

			// Cache variant-dependent data once. Collision is queried often.
			byte h8 = 8;
			var heightValue = Variant?["h8"];
			if (!string.IsNullOrEmpty(heightValue) && byte.TryParse(heightValue, out var hv)) h8 = GameMath.Clamp(hv, (byte)1, (byte)8);

			cachedSelectionBoxes = BoxesByH8[h8];

			bool passThrough = Attributes?["passThrough"].AsBool(false) == true;
			cachedCollisionBoxes = passThrough ? Array.Empty<Cuboidf>() : cachedSelectionBoxes;

			var directionValue = Variant?["dir"];
			if (!string.IsNullOrEmpty(directionValue)) { cachedFlowDir = BlockFacing.FromFirstLetter(directionValue) ?? BlockFacing.FromCode(directionValue); }
			cachedSys = api?.ModLoader?.GetModSystem<ModSystemCollisionFlowFields>();
		}

		// Selectable so players to enable mining/breaking | Selection matches the defined h8 height value
		public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos) { return cachedSelectionBoxes; }

		// Cached on load to avoid per-query variant parsing/allocations
		public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos) { return cachedCollisionBoxes; }


		#region Block Proxy System
		static BlockFacing? GetFlowDir(Block flowDirBlock)
		{
			if (flowDirBlock is BlockFlowCollider flowCollider && flowCollider.CachedFlowDir != null) return flowCollider.CachedFlowDir;

			var flowDirectionVariant = flowDirBlock?.Variant?["dir"];
			if (string.IsNullOrEmpty(flowDirectionVariant)) return null;
			return BlockFacing.FromFirstLetter(flowDirectionVariant) ?? BlockFacing.FromCode(flowDirectionVariant);
		}

		bool TryResolveKnownSource(IBlockAccessor blockAccessor, BlockPos colliderPos, out BlockPos sourcePos, out Block sourceBlock)
		{
			sourcePos = colliderPos?.Copy() ?? new BlockPos();
			sourceBlock = null!; if (blockAccessor == null || colliderPos == null) return false;

			var flowfieldSystem = cachedSys ?? api?.ModLoader?.GetModSystem<ModSystemCollisionFlowFields>();
			if (flowfieldSystem == null) return false;

			// First step: move from this collider to the next block along its pointer
			var firstDir = cachedFlowDir;
			if (firstDir == null) return false;

			sourcePos.Add(firstDir);
			for (int i = 0; i < 4096; i++)
			{
				var accessedBlock = blockAccessor.GetBlock(sourcePos);
				if (accessedBlock.Id == 0) return false;

				if (accessedBlock is BlockFlowCollider)
				{
					var face = GetFlowDir(accessedBlock);
					if (face == null) return false;
					sourcePos.Add(face);
					continue;
				}

				if (!flowfieldSystem.IsKnownSourceBlock(accessedBlock)) return false;

				sourceBlock = accessedBlock;
				return true;
			}

			return false;
		}

		public override float GetResistance(IBlockAccessor blockAccessor, BlockPos pos)
		{
			if (blockAccessor != null && pos != null && TryResolveKnownSource(blockAccessor, pos, out var srcPos, out var srcBlock))
			{
				return srcBlock.GetResistance(blockAccessor, srcPos);
			}
			return base.GetResistance(blockAccessor, pos);
		}

		public override EnumBlockMaterial GetBlockMaterial(IBlockAccessor blockAccessor, BlockPos pos, ItemStack stack = null)
		{
			if (blockAccessor != null && pos != null && TryResolveKnownSource(blockAccessor, pos, out var srcPos, out var srcBlock))
			{
				return srcBlock.GetBlockMaterial(blockAccessor, srcPos, stack);
			}
			return base.GetBlockMaterial(blockAccessor, pos, stack);
		}

		public override string GetPlacedBlockName(IWorldAccessor world, BlockPos pos)
		{
			if (world?.BlockAccessor != null && TryResolveKnownSource(world.BlockAccessor, pos, out var srcPos, out var srcBlock))
			{
				return srcBlock.GetPlacedBlockName(world, srcPos);
			}
			
			return base.GetPlacedBlockName(world, pos);
		}

		public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
		{
			if (world?.BlockAccessor != null && TryResolveKnownSource(world.BlockAccessor, pos, out var srcPos, out var srcBlock))
			{ return srcBlock.GetPlacedBlockInfo(world, srcPos, forPlayer); }

			return base.GetPlacedBlockInfo(world, pos, forPlayer);
		}

		public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos) // Return source block, not a collider that might crash us on read attempt
		{
			var blockAccessor = world?.BlockAccessor;
			if (blockAccessor != null && pos != null && TryResolveKnownSource(blockAccessor, pos, out var srcPos, out var srcBlock)) return new ItemStack(srcBlock, 1);
			return base.OnPickBlock(world, pos);
		}
		#endregion

		public override float OnGettingBroken(IPlayer player, BlockSelection blockSel, ItemSlot itemslot, float remainingResistance, float deltaTime, int counter)
		{
			if (blockSel == null || blockSel.Position == null) return remainingResistance;

			// Vanilla Block.OnGettingBroken assumes HitPosition is non-null (used for sound position)
			if (blockSel.HitPosition == null) blockSel.HitPosition = new Vec3d(0.5, 0.5, 0.5);

			// Resolve to the owning/source block, then emulate vanilla breaking using the source block's rules.
			var blockAccessor = api?.World?.BlockAccessor ?? player?.Entity?.World?.BlockAccessor;
			if (blockAccessor == null) return remainingResistance;

			// Fallback to avoid crashes
			if (!TryResolveKnownSource(blockAccessor, blockSel.Position, out var srcPos, out var srcBlock) || srcPos == null || srcBlock == null)
			{
				float res = remainingResistance - deltaTime;
				return res;
			}

			// Use a selection that points to the SOURCE for tool speed/tier logic
			var sourceSelection = blockSel.Clone();
			sourceSelection.Position = srcPos.Copy();
			sourceSelection.Block = srcBlock;
			if (sourceSelection.HitPosition == null) sourceSelection.HitPosition = blockSel.HitPosition.Clone();

			IItemStack stack = player.InventoryManager.ActiveHotbarSlot.Itemstack;
			float resistance = remainingResistance;

			// Same logic as Block.OnGettingBroken, but driven by the source block's mining rules
			if (srcBlock.RequiredMiningTier == 0)
			{
				if (deltaTime > 0)
				{
					foreach (BlockBehavior behavior in srcBlock.BlockBehaviors) { deltaTime *= behavior.GetMiningSpeedModifier(api.World, srcPos, player); }
				}

				resistance -= deltaTime;
			}

			// Tool logic will read srcSel.Block / srcSel.Position, so it uses the source's tier/material/sounds
			if (stack != null) { resistance = stack.Collectible.OnBlockBreaking(player, sourceSelection, itemslot, remainingResistance, deltaTime, counter); }

			// Play hit/break sounds at the collider position, but use the source block's sounds
			string throttleKey = "totalMsBlockBreaking-" + (player?.PlayerUID ?? player?.PlayerName ?? "unknown");
			long totalMsBreaking = 0;
			if (api.ObjectCache.TryGetValue(throttleKey, out object val)) { totalMsBreaking = (long)val; }

			long nowMs = api.World.ElapsedMilliseconds;

			if (nowMs - totalMsBreaking > 225 || resistance <= 0)
			{
				double posx = blockSel.Position.X + blockSel.HitPosition.X;
				double posy = blockSel.Position.InternalY + blockSel.HitPosition.Y;
				double posz = blockSel.Position.Z + blockSel.HitPosition.Z;

				BlockSounds sounds = srcBlock.GetSounds(api.World.BlockAccessor, sourceSelection);
				player.Entity.World.PlaySoundAt
				(
					resistance > 0 ? sounds.GetHitSound(player) : sounds.GetBreakSound(player),
					posx, posy, posz,
					player,
					srcBlock.RandomSoundPitch(api.World),
					16, 1
				);

				api.ObjectCache[throttleKey] = nowMs;
			}

			// Mirror the block damage overlay onto the source so cracks (and block-breaking particles) match the real block. Client-side.
			if (api is ICoreClientAPI clientAPI) { clientAPI.World.CloneBlockDamage(blockSel.Position, srcPos); }

			return resistance;
		}

		// Overwrite disable drops/breaking, this is handled by our back-propagation, not by the collision block itself. Avoids breaking the flow field!
		// Under no circumstance can OnBlockBroken be allowed to execute as normal, otherwise we could break a chain in the link of the flow field,
		// which could then cause back-propagation to early out without reaching the entire collider set, leaving behind permanent residue.
		public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1f) { }
		public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1f) => Array.Empty<ItemStack>();
	}

	public class ModSystemCollisionFlowFields : ModSystem
	{
		ICoreAPI api = null!;
		ICoreServerAPI serverAPI = null!;

		public const string AttrKey = "collisionFlowField";
		static readonly string[] DirLetters = { "n", "e", "s", "w", "u", "d" };

		
		readonly Dictionary<int, FlowDef> defsById = new(); // Fast runtime lookup: source blockId -> flow definition (cells + mode)
		public bool IsKnownSourceBlock(Block targetBlock) => targetBlock != null && defsById.ContainsKey(targetBlock.Id);

		// Cache of our own flow collider blocks [dirIndex, h8]
		readonly Block?[,] flowBlocksSolid = new Block?[6, 9];
		readonly Block?[,] flowBlocksPass = new Block?[6, 9];

		public override void Start(ICoreAPI api)
		{
			this.api = api;
			api.RegisterBlockClass("BlockFlowCollider", typeof(BlockFlowCollider));
		}

		public override void AssetsFinalize(ICoreAPI api) // Build defs from block JSON attributes
		{
			defsById.Clear();

			var blocks = api.World?.Blocks;
			if (blocks != null)
			{
				foreach (var block in blocks)
				{
					if (block?.Code == null) continue;
					var flowDef = block.Attributes?[AttrKey];
					if (flowDef == null || !flowDef.Exists) continue;

					var flowfieldArray = SelectFlowFieldArray(flowDef, block);
					if (flowfieldArray == null || flowfieldArray.Length == 0) continue;

					var cellList = new List<FlowCell>(flowfieldArray.Length);
					for (int index = 0; index < flowfieldArray.Length; index++)
					{
						JsonObject entry = flowfieldArray[index];

						int[]? pos = entry["pos"].AsArray<int>(null) ?? entry["offset"].AsArray<int>(null);
						if (pos == null || pos.Length < 3) continue;

						int dirIndex = entry["dir"].AsInt(-1);
						if ((uint)dirIndex > 5u) { continue; } // Invalid entry, skip (currently no warning)

						int h8 = entry["h8"].AsInt(8);
						cellList.Add(new FlowCell(pos[0], pos[1], pos[2], dirIndex, h8));
					}

					if (cellList.Count > 0) defsById[block.Id] = new FlowDef(cellList.ToArray(), GetMode(block));
				}
			}

			// Cache our flow collider blocks
			// Solid		== flowcollider-{dir}-{h8}
			// PassThrough	== flowcolliderghost-{dir}-{h8}
			for (int dir = 0; dir < 6; dir++)
			{
				for (int h8 = 1; h8 <= 8; h8++)
				{
					flowBlocksSolid[dir, h8] = api.World.GetBlock(new AssetLocation("collisionflowfields", $"flowcollider-{DirLetters[dir]}-{h8}"));
					flowBlocksPass[dir, h8] = api.World.GetBlock(new AssetLocation("collisionflowfields", $"flowcolliderghost-{DirLetters[dir]}-{h8}"));
				}
			}
		}

		public override void StartServerSide(ICoreServerAPI api)
		{
			serverAPI = api;
			serverAPI.Event.DidPlaceBlock += OnDidPlaceBlock;
			serverAPI.Event.DidBreakBlock += OnDidBreakBlock;
		}

		Block? GetFlowBlock(FlowColliderMode mode, byte dir, byte h8)
		{
			return mode == FlowColliderMode.PassThrough ? flowBlocksPass[dir, h8] : flowBlocksSolid[dir, h8];
		}

		// Used to check if a block with a flow field collider set would actually fit at the target location
		public bool CanPlace(IBlockAccessor blockAccessor, BlockPos sourcePos, Block sourceBlock)
		{
			if (!defsById.TryGetValue(sourceBlock.Id, out var def)) return true;

			var cells = def.Cells;
			var mode = def.Mode;

			foreach (var currentCell in cells)
			{
				if (currentCell.dx == 0 && currentCell.dy == 0 && currentCell.dz == 0) continue;

				var p = sourcePos.AddCopy(currentCell.dx, currentCell.dy, currentCell.dz);
				if (!blockAccessor.IsValidPos(p)) return false;

				var flow = GetFlowBlock(mode, currentCell.dir, currentCell.h8);
				if (flow == null) return false;

				var existing = blockAccessor.GetBlock(p);
				if (!existing.IsReplacableBy(sourceBlock)) return false; // Standard placement rule | Test replacability as if placing the source block
			}

			return true;
		}

		void OnDidPlaceBlock(IServerPlayer byPlayer, int oldblockId, BlockSelection blockSel, ItemStack withItemStack)
		{
			var position = blockSel.Position;
			var blockAccessor = serverAPI.World.BlockAccessor;
			var placed = blockAccessor.GetBlock(position);

			if (!defsById.TryGetValue(placed.Id, out var def)) return;
			if (!CanPlace(blockAccessor, position, placed)) return;

			var cells = def.Cells;
			var mode = def.Mode;

			foreach (var currentCell in cells)
			{
				if (currentCell.dx == 0 && currentCell.dy == 0 && currentCell.dz == 0) continue;

				var cellPosition = position.AddCopy(currentCell.dx, currentCell.dy, currentCell.dz);
				var flow = GetFlowBlock(mode, currentCell.dir, currentCell.h8);
				if (flow != null)
				{
					var existing = blockAccessor.GetBlock(cellPosition);
					if (existing.EntityClass != null) blockAccessor.RemoveBlockEntity(cellPosition);
					blockAccessor.SetBlock(flow.Id, cellPosition);
				}
			}
		}

		void OnDidBreakBlock(IServerPlayer byPlayer, int oldblockId, BlockSelection blockSel)
		{
			var oldBlock = serverAPI.World.Blocks[oldblockId];
			var position = blockSel.Position;

			// If a flow collider is broken, then follow the pointers until we reach the source to relay the breaking to it
			if (oldBlock is BlockFlowCollider)
			{
				var face = GetFlowDir(oldBlock);
				if (face == null) return;

				var current = position.Copy();
				current.Add(face);
				for (int i = 0; i < 4096; i++)
				{
					var accessedBlock = serverAPI.World.BlockAccessor.GetBlock(current);
					if (accessedBlock.Id == 0) return;

					if (accessedBlock is BlockFlowCollider)
					{
						var f2 = GetFlowDir(accessedBlock);
						if (f2 == null) return;
						current.Add(f2);
						continue;
					}

					// Only break targets that are known sources (safety)
					if (defsById.ContainsKey(accessedBlock.Id))
					{
						// Note: BlockAccessor.BreakBlock() does NOT raise sapi.Event.DidBreakBlock,
						// so the normal cleanup handler would not run. We must cleanup explicitly here.
						serverAPI.World.BlockAccessor.BreakBlock(current, byPlayer);
						RemoveFlowCollidersPointingTo(current);
					}

					return;
				}

				return;
			}

			// When the source is broken, back-propagate across the flow field and clean house
			if (!defsById.ContainsKey(oldblockId)) return;
			RemoveFlowCollidersPointingTo(position);
		}
		
		void RemoveFlowCollidersPointingTo(BlockPos sourcePos)
		{
			var blockAccessor = serverAPI.World.BlockAccessor;
			var positionQueue = new Queue<BlockPos>();
			var seen = new HashSet<BlockPos>();
			var remove = new List<BlockPos>();

			// We only care about inbound pointers. Ignore the fact that we may have become air by now.
			var start = sourcePos.Copy();
			positionQueue.Enqueue(start);
			seen.Add(start);

			while (positionQueue.Count > 0)
			{
				var current = positionQueue.Dequeue();

				foreach (var face in BlockFacing.ALLFACES)
				{
					var neighbourPos = current.AddCopy(face);
					var neighbourBlock = blockAccessor.GetBlock(neighbourPos);
					if (neighbourBlock is not BlockFlowCollider) continue;

					var neighbourDirection = GetFlowDir(neighbourBlock);
					if (neighbourDirection == null || neighbourDirection.Index != face.Opposite.Index) continue; // Neighbor must point back to current node

					var positionCopy = neighbourPos.Copy();
					if (!seen.Add(positionCopy)) continue;

					positionQueue.Enqueue(positionCopy);
					remove.Add(positionCopy);
				}
			}

			foreach (var removalPosition in remove)
			{
				if (blockAccessor.GetBlock(removalPosition).EntityClass != null) blockAccessor.RemoveBlockEntity(removalPosition);
				blockAccessor.SetBlock(0, removalPosition);
			}
		}

		static FlowColliderMode GetMode(Block targetBlock)
		{
			if (targetBlock?.Attributes == null) return FlowColliderMode.Solid;

			// String values for multi-attribute changes, keywords act as a template (currently only passthrough, so not very useful)
			string modeString = targetBlock.Attributes["collisionFlowFieldMode"].AsString(null);
			if (!string.IsNullOrEmpty(modeString)) // No string validation, I expect modders to not make spelling mistakes
			{
				if (modeString == "passthrough" ) { return FlowColliderMode.PassThrough; }
				return FlowColliderMode.Solid;
			}

			// Individual attribute bools for overwriting specific aspects after (optionally) using a template
			bool passThrough = targetBlock.Attributes["collisionFlowFieldPassThrough"].AsBool(false);
			return passThrough ? FlowColliderMode.PassThrough : FlowColliderMode.Solid;
		}

		static JsonObject[]? SelectFlowFieldArray(JsonObject root, Block selectBlock)
		{
			if (root == null || !root.Exists) return null;
			if (root.IsArray()) return root.AsArray();

			// Variants each have unique codes, thus exact key matching is sufficient and predictable for our use
			JsonObject selected = root[selectBlock.Code.ToString()];
			if (!selected.Exists) selected = root[selectBlock.Code.Path];
			if (!selected.Exists) selected = root["*"];

			return selected.Exists && selected.IsArray() ? selected.AsArray() : null;
		}

		static BlockFacing? GetFlowDir(Block targetBlock)
		{
			if (targetBlock is BlockFlowCollider flowCollider && flowCollider.CachedFlowDir != null) return flowCollider.CachedFlowDir;

			var directionVariant = targetBlock?.Variant?["dir"];
			if (string.IsNullOrEmpty(directionVariant)) return null;
			return BlockFacing.FromFirstLetter(directionVariant) ?? BlockFacing.FromCode(directionVariant);
		}
	}
}