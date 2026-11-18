# Native reference verification

Checked 2026-11-13 (customization natives 2026-11-14) against the primary CitizenFX native reference. The code contains only hashes, ABI declarations and original invocation glue, not copied native SDK implementations.

| Native / hash | Primary reference |
|---|---|
| SET_PLAYER_MODEL / 00A1CADD00108836 | [PLAYER/SetPlayerModel](https://github.com/citizenfx/natives/blob/master/PLAYER/SetPlayerModel.md) |
| PLAYER_ID / 4F8644AF03D0E0D6 | [PLAYER/PlayerId](https://github.com/citizenfx/natives/blob/master/PLAYER/PlayerId.md) |
| PLAYER_PED_ID / D80958FC74E988A6 | [PLAYER/PlayerPedId](https://github.com/citizenfx/natives/blob/master/PLAYER/PlayerPedId.md) |
| IS_PLAYER_PLAYING / 5E9564D8246B909A | [PLAYER/IsPlayerPlaying](https://github.com/citizenfx/natives/blob/master/PLAYER/IsPlayerPlaying.md) |
| IS_ENTITY_DEAD / 5F9532F3B5CC2551 | [ENTITY/IsEntityDead](https://github.com/citizenfx/natives/blob/master/ENTITY/IsEntityDead.md) |
| NETWORK_IS_SESSION_STARTED / 9DE624D2FC4B603F | [NETWORK/NetworkIsSessionStarted](https://github.com/citizenfx/natives/blob/master/NETWORK/NetworkIsSessionStarted.md) |
| NETWORK_IS_SESSION_ACTIVE / D83C2B94E7508980 | [NETWORK/NetworkIsSessionActive](https://github.com/citizenfx/natives/blob/master/NETWORK/NetworkIsSessionActive.md) |
| IS_MODEL_IN_CDIMAGE / 35B9E0803292B641 | [STREAMING/IsModelInCdimage](https://github.com/citizenfx/natives/blob/master/STREAMING/IsModelInCdimage.md) |
| IS_MODEL_VALID / C0296A2EDF545E92 | [STREAMING/IsModelValid](https://github.com/citizenfx/natives/blob/master/STREAMING/IsModelValid.md) |
| IS_MODEL_A_PED / 75816577FEA6DAD5 | [STREAMING/IsModelAPed](https://github.com/citizenfx/natives/blob/master/STREAMING/IsModelAPed.md) |
| REQUEST_MODEL / 963D27A58DF860AC | [STREAMING/RequestModel](https://github.com/citizenfx/natives/blob/master/STREAMING/RequestModel.md) |
| HAS_MODEL_LOADED / 98A4EB5D89A0C952 | [STREAMING/HasModelLoaded](https://github.com/citizenfx/natives/blob/master/STREAMING/HasModelLoaded.md) |
| SET_MODEL_AS_NO_LONGER_NEEDED / E532F5D78798DAAB | [STREAMING/SetModelAsNoLongerNeeded](https://github.com/citizenfx/natives/blob/master/STREAMING/SetModelAsNoLongerNeeded.md) |
| SET_PED_DEFAULT_COMPONENT_VARIATION / 45EEE61580806D63 | [PED/SetPedDefaultComponentVariation](https://github.com/citizenfx/natives/blob/master/PED/SetPedDefaultComponentVariation.md) |
| DISABLE_ALL_CONTROL_ACTIONS / 5F4B6931816E599B | [PAD/DisableAllControlActions](https://github.com/citizenfx/natives/blob/master/PAD/DisableAllControlActions.md) |
| DISABLE_CONTROL_ACTION / FE99B66D079CF6BC | [PAD/DisableControlAction](https://github.com/citizenfx/natives/blob/master/PAD/DisableControlAction.md) |
| DRAW_RECT / 3A618A217E5154F0 | [GRAPHICS/DrawRect](https://github.com/citizenfx/natives/blob/master/GRAPHICS/DrawRect.md) |
| BEGIN_TEXT_COMMAND_DISPLAY_TEXT / 25FBB336DF1804CB | [HUD/BeginTextCommandDisplayText](https://github.com/citizenfx/natives/blob/master/HUD/BeginTextCommandDisplayText.md) |
| ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME / 6C188BE134E074AA | [HUD/AddTextComponentSubstringPlayerName](https://github.com/citizenfx/natives/blob/master/HUD/AddTextComponentSubstringPlayerName.md) |
| END_TEXT_COMMAND_DISPLAY_TEXT / CD015E5BB0D96A57 | [HUD/EndTextCommandDisplayText](https://github.com/citizenfx/natives/blob/master/HUD/EndTextCommandDisplayText.md) |
| SET_TEXT_FONT / 66E0276CC5F6B9DA | [HUD/SetTextFont](https://github.com/citizenfx/natives/blob/master/HUD/SetTextFont.md) |
| SET_TEXT_SCALE / 07C837F9A01C34C9 | [HUD/SetTextScale](https://github.com/citizenfx/natives/blob/master/HUD/SetTextScale.md) |
| SET_TEXT_COLOUR / BE6B23FFA53FB442 | [HUD/SetTextColour](https://github.com/citizenfx/natives/blob/master/HUD/SetTextColour.md) |
| SET_TEXT_JUSTIFICATION / 4E096588B13FFECA | [HUD/SetTextJustification](https://github.com/citizenfx/natives/blob/master/HUD/SetTextJustification.md) |
| SET_TEXT_WRAP / 63145D9C883A1A70 | [HUD/SetTextWrap](https://github.com/citizenfx/natives/blob/master/HUD/SetTextWrap.md) |
| BEGIN_TEXT_COMMAND_GET_SCREEN_WIDTH_OF_DISPLAY_TEXT / 54CE8AC98E120CAB | [HUD/BeginTextCommandGetScreenWidthOfDisplayText](https://github.com/citizenfx/natives/blob/master/HUD/BeginTextCommandGetScreenWidthOfDisplayText.md) |
| END_TEXT_COMMAND_GET_SCREEN_WIDTH_OF_DISPLAY_TEXT / 85F061DA64ED2F67 | [HUD/EndTextCommandGetScreenWidthOfDisplayText](https://github.com/citizenfx/natives/blob/master/HUD/EndTextCommandGetScreenWidthOfDisplayText.md) |
| GET_RENDERED_CHARACTER_HEIGHT / DB88A37483346780 | [HUD/GetRenderedCharacterHeight](https://github.com/citizenfx/natives/blob/master/HUD/GetRenderedCharacterHeight.md) |
| RESET_SCRIPT_GFX_ALIGN / E3A3DB414A373DAB | [GRAPHICS/ResetScriptGfxAlign](https://github.com/citizenfx/natives/blob/master/GRAPHICS/ResetScriptGfxAlign.md) |
| GET_ENTITY_MODEL / 9F47B058362C84B5 | [ENTITY/GetEntityModel](https://github.com/citizenfx/natives/blob/master/ENTITY/GetEntityModel.md) |
| GET_NUMBER_OF_PED_DRAWABLE_VARIATIONS / 27561561732A7842 | [PED/GetNumberOfPedDrawableVariations](https://github.com/citizenfx/natives/blob/master/PED/GetNumberOfPedDrawableVariations.md) |
| GET_NUMBER_OF_PED_TEXTURE_VARIATIONS / 8F7156A3142A6BAD | [PED/GetNumberOfPedTextureVariations](https://github.com/citizenfx/natives/blob/master/PED/GetNumberOfPedTextureVariations.md) |
| GET_PED_DRAWABLE_VARIATION / 67F3780DD425D4FC | [PED/GetPedDrawableVariation](https://github.com/citizenfx/natives/blob/master/PED/GetPedDrawableVariation.md) |
| GET_PED_TEXTURE_VARIATION / 04A355E041E004E6 | [PED/GetPedTextureVariation](https://github.com/citizenfx/natives/blob/master/PED/GetPedTextureVariation.md) |
| GET_PED_PALETTE_VARIATION / E3DD5F2A84B42281 | [PED/GetPedPaletteVariation](https://github.com/citizenfx/natives/blob/master/PED/GetPedPaletteVariation.md) |
| SET_PED_COMPONENT_VARIATION / 262B14F48D29DE80 | [PED/SetPedComponentVariation](https://github.com/citizenfx/natives/blob/master/PED/SetPedComponentVariation.md) |
| IS_PED_COMPONENT_VARIATION_VALID / E825F6B6CEA7671D | [PED/IsPedComponentVariationValid](https://github.com/citizenfx/natives/blob/master/PED/IsPedComponentVariationValid.md) |
| GET_NUMBER_OF_PED_PROP_DRAWABLE_VARIATIONS / 5FAF9754E789FB47 | [PED/GetNumberOfPedPropDrawableVariations](https://github.com/citizenfx/natives/blob/master/PED/GetNumberOfPedPropDrawableVariations.md) |
| GET_NUMBER_OF_PED_PROP_TEXTURE_VARIATIONS / A6E7F1CEB523E171 | [PED/GetNumberOfPedPropTextureVariations](https://github.com/citizenfx/natives/blob/master/PED/GetNumberOfPedPropTextureVariations.md) |
| GET_PED_PROP_INDEX / 898CC20EA75BACD8 | [PED/GetPedPropIndex](https://github.com/citizenfx/natives/blob/master/PED/GetPedPropIndex.md) |
| GET_PED_PROP_TEXTURE_INDEX / E131A28626F81AB2 | [PED/GetPedPropTextureIndex](https://github.com/citizenfx/natives/blob/master/PED/GetPedPropTextureIndex.md) |
| SET_PED_PROP_INDEX / 93376B65A266EB5F | [PED/SetPedPropIndex](https://github.com/citizenfx/natives/blob/master/PED/SetPedPropIndex.md) |
| CLEAR_PED_PROP / 0943E5B8E078E76E | [PED/ClearPedProp](https://github.com/citizenfx/natives/blob/master/PED/ClearPedProp.md) |
| CLEAR_ALL_PED_PROPS / CD8A7537A9B52F06 | [PED/ClearAllPedProps](https://github.com/citizenfx/natives/blob/master/PED/ClearAllPedProps.md) |

Model-change sequencing follows the reference's request-before-change and invalidated-old-ped contract. Dead, rectangle and text-display invocations include the additional parameters documented in NativeDB notes (false, false and 0 respectively). Control indices are checked against the [CitizenFX control mapping](https://docs.fivem.net/docs/game-references/controls/).
