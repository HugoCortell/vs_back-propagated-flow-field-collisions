using Vintagestory.API.Common;

namespace CollisionFlowFields
{
	// Here is a minimal example of how to perform a flow field collision check to validate block placement
	public class BlockFlowfieldSampleStatue : Block
	{
		public override bool CanPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref string failureCode)
		{
			if (!base.CanPlaceBlock(world, byPlayer, blockSel, ref failureCode)) return false;

			var collisionflowfields = api.ModLoader.GetModSystem<ModSystemCollisionFlowFields>();
			if (collisionflowfields != null && !collisionflowfields.CanPlace(world.BlockAccessor, blockSel.Position, this))
			{
				failureCode = "notreplaceable"; // This is a vanilla error code, it will auto translate
				return false;
			}

			return true;
		}
	}

	// Make sure to register your block, of course
	public class ModSystemFlowfieldSample : ModSystem
	{
		public override void Start(ICoreAPI api) { api.RegisterBlockClass("BlockFlowfieldSampleStatue", typeof(BlockFlowfieldSampleStatue)); }
	}
}