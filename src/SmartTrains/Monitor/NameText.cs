using System;
using System.Reflection;
using Game.SceneFlow;
using Game.UI;
using Unity.Entities;

namespace SmartTrains.Monitor
{
    /// <summary>
    /// An entity's name as plain text in the current language, for the log
    /// and for records that outlive the entity, e.g. a removed train.
    ///
    /// The panel shows live names through the game's own name format (see
    /// TrainsUISystem.WriteName). Text needs more: line names are templates
    /// such as "Güterzugroute {NUMBER}", and the game fills them in only in
    /// its UI. The fields that hold template and arguments are private, so
    /// they are read by reflection. If a game update renames them, names fall
    /// back to NameSystem.GetRenderedLabelName, which leaves templates
    /// unfilled but still says what the entity is.
    /// </summary>
    internal static class NameText
    {
        private const BindingFlags kPrivate = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly FieldInfo s_Type = typeof(NameSystem.Name).GetField("m_NameType", kPrivate);
        private static readonly FieldInfo s_Id = typeof(NameSystem.Name).GetField("m_NameID", kPrivate);
        private static readonly FieldInfo s_Args = typeof(NameSystem.Name).GetField("m_NameArgs", kPrivate);

        /// <summary>The name, or "–" for no entity.</summary>
        public static string Of(NameSystem names, EntityManager entityManager, Entity entity)
        {
            if (entity == Entity.Null || !entityManager.Exists(entity))
                return "–";
            if (s_Type == null || s_Id == null)
                return names.GetRenderedLabelName(entity);
            try
            {
                object name = names.GetName(entity);
                string id = (string)s_Id.GetValue(name) ?? "";
                switch ((NameSystem.NameType)s_Type.GetValue(name))
                {
                    case NameSystem.NameType.Custom:
                        return id;
                    case NameSystem.NameType.Localized:
                        return Localize(id);
                    default:
                        return Fill(Localize(id), s_Args?.GetValue(name) as string[]);
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warn(e, $"Could not build the name of entity {entity.Index}; using the game's label.");
                return names.GetRenderedLabelName(entity);
            }
        }

        /// <summary>
        /// Puts the arguments into a template. The game stores them as pairs
        /// of placeholder and value, e.g. "NUMBER", "5"; a value may itself be
        /// a text ID, e.g. of a street name.
        /// </summary>
        private static string Fill(string template, string[] args)
        {
            if (args == null)
                return template;
            for (int i = 0; i + 1 < args.Length; i += 2)
                template = template.Replace("{" + args[i] + "}", Localize(args[i + 1]));
            return template;
        }

        /// <summary>The text for an ID in the current language, as NameSystem.GetRenderedLabelName looks it up; the ID itself if there is none.</summary>
        private static string Localize(string id)
        {
            if (string.IsNullOrEmpty(id))
                return "";
            return GameManager.instance.localizationManager.activeDictionary.TryGetValue(id, out string text) ? text : id;
        }
    }
}
