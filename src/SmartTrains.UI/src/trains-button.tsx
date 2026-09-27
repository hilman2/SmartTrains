import { useValue } from "cs2/api";
import { Button } from "cs2/ui";
import { panelOpen$, setPanelOpen } from "bindings";
import { useTranslate } from "localization";
import icon from "images/trains.svg";

/** Toolbar button in the top left corner that opens and closes the panel. */
export const TrainsButton = () => {
  const open = useValue(panelOpen$);
  const { t } = useTranslate();
  return (
    <Button
      variant="floating"
      src={icon}
      selected={open}
      tooltipLabel={t("Panel.ButtonHint", "Smart Trains")}
      onSelect={() => setPanelOpen(!open)}
    />
  );
};
