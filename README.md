# Back-Propagated Flow Field Collisions for Vintage Story
For modders too stupid to understand the multi-block system.

<img src="https://moddbcdn.vintagestory.at/unfair_comparison_ev_71a519ee3afe8ec9c75c03e43b5c6416.gif" alt="Comparison of vanilla VS. BPFFC" width="480">

## Using this in your mod

1. Add this to your mod info in order to mark the mod as a dependency
```json
"dependencies": {
	"collisionflowfields": "(the latest version)"
}
```
2. Add the actual dependency to your mod's CSProj
```XML
<ItemGroup>
	<Reference Include="CollisionFlowFields">
		<HintPath>Dependencies\\CollisionFlowFields.dll</HintPath>
		<Private>false</Private>
	</Reference>
</ItemGroup>
```
4. Make sure to have actually put a copy of the mod's dll into your dependencies folder
5. Profit

Remember to always overwrite CanPlaceBlock for your BPFFC-enabled blocks, otherwise you might get malformed flowfields due to invalid block placement
Now watch this youtube video because it covers it better than I could write it down: 
