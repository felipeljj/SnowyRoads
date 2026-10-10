/*
 * Cities: Skylines II UI Module
 * Id: RoadSnow.Options
 * Author: RoadSnow
 * Version: 1.0
 * Dependencies:
 */

export default function register(registry) {
    // Reuse the game's picker and widget binding so RGB edits follow the normal settings flow.
    const React = window.React;
    const h = React.createElement;
    const OptionField = registry.get("game-ui/menu/widgets/field/field.tsx", "OptionField");
    const Localized = registry.get("game-ui/common/localization/localized.tsx", "Localized");
    const ColorPicker = registry.get(
        "game-ui/common/input/color-picker/color-picker/color-picker.tsx",
        "ColorPicker",
    );
    const ColorPickerSliderMode = registry.get(
        "game-ui/common/input/color-picker/color-picker/color-picker.tsx",
        "ColorPickerSliderMode",
    );
    const ColorPickerPreview = registry.get(
        "game-ui/common/input/color-picker/color-picker/color-picker.tsx",
        "ColorPickerPreview",
    );
    const rgbaToHsva = registry.get("game-ui/common/color.ts", "rgbaToHsva");
    const hsvaToRgba = registry.get("game-ui/common/color.ts", "hsvaToRgba");
    const formatColor = registry.get("game-ui/common/color.ts", "formatColor");
    const useWidgetId = registry.get("game-ui/widgets/components/widget-props.ts", "useWidgetId");
    const useElementCallback = registry.get(
        "game-ui/common/utils/element-callback.ts",
        "useElementCallback",
    );
    const setValue = registry.get("game-ui/widgets/data-binding/widget-bindings.ts", "setValue");

    function SnowColorOption({ parent, path, props }) {
        const [open, setOpen] = React.useState(false);
        const [color, setColor] = React.useState(props.value);
        const hue = React.useRef(0);
        const submit = useElementCallback(setValue, useWidgetId(parent, path));
        React.useEffect(() => setColor(props.value), [props.value]);
        React.useEffect(() => {
            if (props.disabled) setOpen(false);
        }, [props.disabled]);

        const change = React.useCallback(
            (hsva) => {
                if (props.disabled) return;
                hue.current = hsva.h;
                const next = { ...hsvaToRgba(hsva), a: 1 };
                setColor(next);
                submit(next);
            },
            [submit, props.disabled],
        );
        const label = h(Localized, { value: props.displayName });
        const rgb = [color.r, color.g, color.b].map((v) => Math.round(v * 255)).join(", ");

        return h(
            "div",
            {
                onKeyDown: (event) => {
                    if (event.key === "Escape" && open) {
                        setOpen(false);
                        event.stopPropagation();
                    }
                },
            },
            h(
                OptionField,
                { ...props, id: path, label },
                h(
                    "button",
                    {
                        type: "button",
                        disabled: props.disabled,
                        "aria-expanded": open && !props.disabled,
                        onClick: () => {
                            if (!props.disabled) setOpen((value) => !value);
                        },
                        style: {
                            display: "flex",
                            alignItems: "center",
                            padding: "6rem 10rem",
                            border: "1rem solid rgba(255,255,255,0.35)",
                            borderRadius: "3rem",
                            backgroundColor: "rgba(0,0,0,0.25)",
                            color: "white",
                            opacity: props.disabled ? 0.45 : 1,
                            cursor: props.disabled ? "default" : "pointer",
                        },
                    },
                    h("span", {
                        style: {
                            width: "48rem",
                            height: "24rem",
                            marginRight: "10rem",
                            backgroundColor: formatColor(color),
                            border: "1rem solid rgba(255,255,255,0.5)",
                        },
                    }),
                    `RGB ${rgb}`,
                ),
            ),
            open &&
                !props.disabled &&
                h(
                    "div",
                    {
                        style: {
                            display: "flex",
                            justifyContent: "flex-end",
                            padding: "12rem 18rem",
                            backgroundColor: "rgba(0,0,0,0.2)",
                        },
                    },
                    h(ColorPicker, {
                        focusKey: path + ".picker",
                        color: rgbaToHsva(color, hue.current),
                        alpha: false,
                        colorWheel: true,
                        hexInput: true,
                        sliderTextInput: true,
                        preview: ColorPickerPreview.Current,
                        mode: ColorPickerSliderMode.RgbByte,
                        allowFocusExit: true,
                        onChange: change,
                    }),
                ),
        );
    }

    registry.extend(
        "game-ui/menu/widgets/option-widget-renderer.tsx",
        "optionsWidgetComponents",
        (components) => ({ ...components, "RoadSnow.Mod.SnowColorField": SnowColorOption }),
    );
    console.log("RoadSnow RGB picker loaded.");
}
