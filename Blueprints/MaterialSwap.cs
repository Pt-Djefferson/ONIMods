using PeterHan.PLib.UI;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Blueprints {
    /// <summary>
    /// Lets the player replace the materials stored in a blueprint before placing it.
    /// The dialog lists every building type in the blueprint (with its picture), the material each of its
    /// material slots uses, and a dropdown with the materials that slot accepts. A choice only changes that
    /// building type, so e.g. pipes can switch to Steel while wires keep Aluminum.
    /// </summary>
    public static class MaterialSwap {
        private const string DIALOG_KEY_APPLY = "apply";

        private const float ICON_SIZE = 32f;
        private const float SMALL_ICON_SIZE = 16f;

        /// <summary>
        /// Identifies one row: a building type, one of its material slots, and the material currently in it.
        /// </summary>
        private readonly struct SlotKey : System.IEquatable<SlotKey> {
            public readonly string PrefabID;
            public readonly int Slot;
            public readonly Tag Material;

            public SlotKey(string prefabID, int slot, Tag material) {
                PrefabID = prefabID;
                Slot = slot;
                Material = material;
            }

            public bool Equals(SlotKey other) => PrefabID == other.PrefabID && Slot == other.Slot && Material == other.Material;
            public override bool Equals(object obj) => obj is SlotKey other && Equals(other);
            public override int GetHashCode() => ((PrefabID?.GetHashCode() ?? 0) * 397 ^ Slot) * 397 ^ Material.GetHashCode();
        }

        /// <summary>
        /// Data for one dialog row.
        /// </summary>
        private sealed class SlotGroup {
            public BuildingDef Def;
            public int Slot;
            public int SlotCount;
            public Tag Material;
            public string Category;
            public int Count;
        }

        /// <summary>
        /// One entry in a material dropdown.
        /// </summary>
        private sealed class MaterialOption : IListableOption {
            public Tag Tag { get; }
            private readonly string label;

            public MaterialOption(Tag tag, string label) {
                Tag = tag;
                this.label = label;
            }

            public string GetProperName() => label;
        }

        /// <summary>
        /// Returns true if the element can be used for a building material slot.
        /// Categories may contain several alternatives separated by '&amp;'.
        /// </summary>
        public static bool IsValidForCategory(Element element, string category) {
            if (element == null || string.IsNullOrEmpty(category)) {
                return false;
            }

            foreach (string part in category.Split('&')) {
                Tag categoryTag = new Tag(part.Trim());

                if (element.tag == categoryTag || element.materialCategory == categoryTag || element.HasTag(categoryTag)) {
                    return true;
                }
            }

            return false;
        }

        private static string ElementName(Tag tag) {
            Element element = ElementLoader.GetElement(tag);
            return element != null ? element.name : tag.Name;
        }

        private static string BuildingName(BuildingDef buildingDef) {
            string name = buildingDef.Name;
            return string.IsNullOrEmpty(name) ? buildingDef.PrefabID : STRINGS.UI.StripLinkFormatting(name);
        }

        /// <summary>
        /// Groups the blueprint into rows: building type + material slot + current material.
        /// </summary>
        private static List<SlotGroup> CollectSlotGroups(Blueprint blueprint) {
            var groups = new Dictionary<SlotKey, SlotGroup>();

            foreach (BuildingConfig buildingConfig in blueprint.BuildingConfiguration) {
                BuildingDef def = buildingConfig.BuildingDef;
                if (def == null) {
                    continue;
                }

                string[] categories = def.MaterialCategory;

                for (int i = 0; i < buildingConfig.SelectedElements.Count; ++i) {
                    Tag material = buildingConfig.SelectedElements[i];
                    var key = new SlotKey(def.PrefabID, i, material);

                    if (!groups.TryGetValue(key, out SlotGroup group)) {
                        group = new SlotGroup {
                            Def = def,
                            Slot = i,
                            SlotCount = buildingConfig.SelectedElements.Count,
                            Material = material,
                            Category = categories != null && i < categories.Length ? categories[i] : null
                        };
                        groups[key] = group;
                    }

                    group.Count++;
                }
            }

            return groups.Values
                .OrderByDescending(g => g.Count)
                .ThenBy(g => BuildingName(g.Def))
                .ThenBy(g => g.Slot)
                .ToList();
        }

        /// <summary>
        /// Dropdown options for one row: the current material first, then every enabled solid element the slot
        /// accepts, sorted by the amount available in the active world.
        /// </summary>
        private static List<MaterialOption> BuildOptions(SlotGroup group, Dictionary<Tag, float> available) {
            var options = new List<MaterialOption> {
                new MaterialOption(group.Material, ElementName(group.Material) + " " + (string) BlueprintsStrings.STRING_BLUEPRINTS_MATERIALS_KEEP)
            };

            if (group.Category == null) {
                return options;
            }

            var candidates = new List<(Element element, float amount)>();

            foreach (Element element in ElementLoader.elements) {
                if (element == null || element.disabled || !element.IsSolid || element.tag == group.Material) {
                    continue;
                }

                if (IsValidForCategory(element, group.Category)) {
                    available.TryGetValue(element.tag, out float amount);
                    candidates.Add((element, amount));
                }
            }

            foreach (var (element, amount) in candidates.OrderByDescending(c => c.amount).ThenBy(c => c.element.name)) {
                options.Add(new MaterialOption(element.tag, element.name + "  (" + GameUtil.GetFormattedMass(amount) + ")"));
            }

            return options;
        }

        /// <summary>
        /// Copy of a PLib text style with another size and colour.
        /// </summary>
        private static TextStyleSetting MakeStyle(TextStyleSetting baseStyle, int fontSize, Color color) {
            TextStyleSetting style = Object.Instantiate(baseStyle);
            style.fontSize = fontSize;
            style.textColor = color;
            return style;
        }

        /// <summary>
        /// UI sprite of a building or element, or null if the game has none.
        /// </summary>
        private static Tuple<Sprite, Color> GetSprite(object item) {
            try {
                var sprite = item != null ? Def.GetUISprite(item) : null;
                return sprite != null && sprite.first != null ? sprite : null;
            }

            catch (System.Exception exception) {
                Debug.Log("Blueprints: no icon for " + item + ": " + exception.Message);
                return null;
            }
        }

        private static IUIComponent BuildingIcon(BuildingDef def) {
            var sprite = GetSprite(def.BuildingComplete);

            if (sprite != null) {
                return new PLabel("Icon_" + def.PrefabID) {
                    Sprite = sprite.first,
                    SpriteTint = sprite.second,
                    SpriteSize = new Vector2(ICON_SIZE, ICON_SIZE),
                    Text = null
                };
            }

            return new PSpacer { PreferredSize = new Vector2(ICON_SIZE, ICON_SIZE) };
        }

        /// <summary>
        /// Opens the material replacement dialog for the selected blueprint.
        /// </summary>
        public static void ShowDialog() {
            if (!BlueprintsState.HasBlueprints()) {
                return;
            }

            Blueprint blueprint = BlueprintsState.SelectedBlueprint;
            if (blueprint == null || blueprint.BuildingConfiguration.Count == 0) {
                return;
            }

            List<SlotGroup> groups = CollectSlotGroups(blueprint);
            if (groups.Count == 0) {
                return;
            }

            Dictionary<Tag, float> available = new Dictionary<Tag, float>();
            try {
                available = ClusterManager.Instance.activeWorld.worldInventory.GetAccessibleAmounts();
            }

            catch (System.Exception exception) {
                Debug.Log("Blueprints: could not read world inventory: " + exception.Message);
            }

            TextStyleSetting headerStyle = MakeStyle(PUITuning.Fonts.TextLightStyle, 13, new Color(0.62f, 0.66f, 0.78f));
            TextStyleSetting nameStyle = MakeStyle(PUITuning.Fonts.TextLightStyle, 16, Color.white);
            TextStyleSetting materialStyle = MakeStyle(PUITuning.Fonts.TextLightStyle, 13, new Color(0.75f, 0.78f, 0.86f));

            // Chosen replacement per row. Starts as "keep".
            var choices = new Dictionary<SlotKey, Tag>();

            // Columns: building picture | building name + current material | arrow | new material dropdown
            PGridPanel grid = new PGridPanel("MaterialGrid");
            grid.AddColumn(new GridColumnSpec(ICON_SIZE + 8f, 0f));
            grid.AddColumn(new GridColumnSpec(400f, 1f));
            grid.AddColumn(new GridColumnSpec(28f, 0f));
            grid.AddColumn(new GridColumnSpec(300f, 0f));

            grid.AddRow(new GridRowSpec(0f, 0f));
            grid.AddChild(new PLabel("HeaderBuilding") {
                Text = BlueprintsStrings.STRING_BLUEPRINTS_MATERIALS_HEADER_MATERIAL,
                TextStyle = headerStyle
            }, new GridComponentSpec(0, 1) { Alignment = TextAnchor.MiddleLeft, Margin = new RectOffset(4, 4, 2, 6) });
            grid.AddChild(new PLabel("HeaderNew") {
                Text = BlueprintsStrings.STRING_BLUEPRINTS_MATERIALS_HEADER_NEW,
                TextStyle = headerStyle
            }, new GridComponentSpec(0, 3) { Alignment = TextAnchor.MiddleLeft, Margin = new RectOffset(4, 4, 2, 6) });

            int row = 1;
            foreach (SlotGroup group in groups) {
                var key = new SlotKey(group.Def.PrefabID, group.Slot, group.Material);
                string rowId = group.Def.PrefabID + "_" + group.Slot + "_" + group.Material.Name;
                choices[key] = group.Material;

                List<MaterialOption> options = BuildOptions(group, available);

                grid.AddRow(new GridRowSpec(0f, 0f));

                grid.AddChild(BuildingIcon(group.Def),
                    new GridComponentSpec(row, 0) { Alignment = TextAnchor.MiddleCenter, Margin = new RectOffset(2, 2, 4, 4) });

                // Building name, then the material it is made of (with the material's icon).
                string materialText = group.SlotCount > 1
                    ? string.Format(BlueprintsStrings.STRING_BLUEPRINTS_MATERIALS_SLOT_N, group.Slot + 1, ElementName(group.Material))
                    : string.Format(BlueprintsStrings.STRING_BLUEPRINTS_MATERIALS_SLOT, ElementName(group.Material));
                var materialSprite = GetSprite(ElementLoader.GetElement(group.Material));

                PPanel info = new PPanel("Info_" + rowId) {
                    Direction = PanelDirection.Vertical,
                    Alignment = TextAnchor.MiddleLeft,
                    Spacing = 2
                };
                info.AddChild(new PLabel("Name_" + rowId) {
                    Text = string.Format(BlueprintsStrings.STRING_BLUEPRINTS_MATERIALS_ROW, BuildingName(group.Def), group.Count),
                    TextStyle = nameStyle,
                    TextAlignment = TextAnchor.MiddleLeft
                });
                info.AddChild(new PLabel("Material_" + rowId) {
                    Text = materialText,
                    TextStyle = materialStyle,
                    TextAlignment = TextAnchor.MiddleLeft,
                    Sprite = materialSprite?.first,
                    SpriteTint = materialSprite != null ? materialSprite.second : Color.white,
                    SpriteSize = new Vector2(SMALL_ICON_SIZE, SMALL_ICON_SIZE),
                    SpritePosition = TextAnchor.MiddleLeft,
                    IconSpacing = 4
                });
                grid.AddChild(info,
                    new GridComponentSpec(row, 1) { Alignment = TextAnchor.MiddleLeft, Margin = new RectOffset(4, 8, 4, 4) });

                grid.AddChild(new PLabel("Arrow_" + rowId) {
                    Text = "→",
                    TextStyle = nameStyle
                }, new GridComponentSpec(row, 2) { Alignment = TextAnchor.MiddleCenter });

                grid.AddChild(new PComboBox<MaterialOption>("Combo_" + rowId) {
                    Content = options,
                    InitialItem = options[0],
                    MinWidth = 280,
                    MaxRowsShown = 12,
                    TextStyle = PUITuning.Fonts.TextLightStyle,
                    OnOptionSelected = (_, choice) => {
                        if (choice != null) {
                            choices[key] = choice.Tag;
                        }
                    }
                }.SetKleiBlueStyle(),
                    new GridComponentSpec(row, 3) { Alignment = TextAnchor.MiddleLeft, Margin = new RectOffset(4, 4, 4, 4) });

                row++;
            }

            PDialog dialog = new PDialog("BlueprintsMaterialSwap") {
                Title = string.Format(BlueprintsStrings.STRING_BLUEPRINTS_MATERIALS_TITLE, blueprint.FriendlyName),
                Size = new Vector2(820f, 200f),
                MaxSize = new Vector2(980f, 680f),
                SortKey = 200f,
                DialogClosed = option => {
                    SpeedControlScreen.Instance.Unpause(false);

                    if (option == DIALOG_KEY_APPLY) {
                        Apply(blueprint, choices);
                    }
                }
            };

            dialog.Body.AddChild(new PScrollPane("MaterialScroll") {
                Child = grid,
                ScrollHorizontal = false,
                ScrollVertical = true,
                AlwaysShowVertical = true,
                TrackSize = 8f,
                FlexSize = Vector2.one
            });

            dialog.AddButton(DIALOG_KEY_APPLY, BlueprintsStrings.STRING_BLUEPRINTS_MATERIALS_APPLY, null, PUITuning.Colors.ButtonPinkStyle, PUITuning.Fonts.UILightStyle);
            dialog.AddButton(PDialog.DIALOG_KEY_CLOSE, BlueprintsStrings.STRING_BLUEPRINTS_MATERIALS_CANCEL, null);

            SpeedControlScreen.Instance.Pause(false);
            dialog.Show();
        }

        /// <summary>
        /// Applies the chosen replacements (per building type and slot), saves the blueprint (keeping a
        /// one-time backup of the original file) and refreshes the placement preview.
        /// </summary>
        private static void Apply(Blueprint blueprint, Dictionary<SlotKey, Tag> choices) {
            int changed = 0;
            int skipped = 0;

            foreach (BuildingConfig buildingConfig in blueprint.BuildingConfiguration) {
                BuildingDef def = buildingConfig.BuildingDef;
                if (def == null) {
                    continue;
                }

                string[] categories = def.MaterialCategory;

                for (int i = 0; i < buildingConfig.SelectedElements.Count; ++i) {
                    Tag source = buildingConfig.SelectedElements[i];

                    if (!choices.TryGetValue(new SlotKey(def.PrefabID, i, source), out Tag target) || target == source) {
                        continue;
                    }

                    if (categories != null && i < categories.Length && IsValidForCategory(ElementLoader.GetElement(target), categories[i])) {
                        buildingConfig.SelectedElements[i] = target;
                        changed++;
                    }

                    else {
                        skipped++;
                    }
                }
            }

            if (changed > 0) {
                try {
                    string backupPath = blueprint.FilePath + ".backup";
                    if (File.Exists(blueprint.FilePath) && !File.Exists(backupPath)) {
                        File.Copy(blueprint.FilePath, backupPath);
                    }

                    blueprint.DeleteFile();
                    blueprint.Write();
                }

                catch (System.Exception exception) {
                    Debug.Log("Blueprints: could not save blueprint after material swap: " + exception.Message);
                }

                blueprint.CacheCost();
                BlueprintsState.ClearVisuals();
                BlueprintsState.VisualizeBlueprint(Grid.PosToXY(PlayerController.GetCursorPos(KInputManager.GetMousePos())), blueprint);
            }

            PopFXManager.Instance.SpawnFX(BlueprintsAssets.BLUEPRINTS_USE_ICON_SPRITE,
                string.Format(BlueprintsStrings.STRING_BLUEPRINTS_MATERIALS_DONE, changed, skipped),
                null, PlayerController.GetCursorPos(KInputManager.GetMousePos()), BlueprintsAssets.Options.FXTime);
        }
    }
}
