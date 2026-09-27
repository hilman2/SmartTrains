// Builds the Smart Trains panel into a single ES module, <id>.mjs, plus its
// CSS. Adapted from the game's UI mod template (Cities2_Data/Content/Game/
// .ModdingToolchain/npx-create-csii-ui-mod). The only change is the output
// folder: SMARTTRAINS_UI_OUT if set (the Docker build writes there), else the
// template's default, the game's local Mods folder.
const path = require("path");
const MOD = require("./mod.json");
const MiniCssExtractPlugin = require("mini-css-extract-plugin");
const { CSSPresencePlugin } = require("./tools/css-presence");
const TerserPlugin = require("terser-webpack-plugin");

const OUTPUT_DIR =
  process.env.SMARTTRAINS_UI_OUT ||
  (process.env.CSII_USERDATAPATH && `${process.env.CSII_USERDATAPATH}\\Mods\\${MOD.id}`);

if (!OUTPUT_DIR) {
  throw "Set SMARTTRAINS_UI_OUT, or install the game's modding toolchain so that CSII_USERDATAPATH is set.";
}

// The game reads the module's id, author and version from this comment at
// the top of the bundle. Without it the module registers with an empty id.
const banner = `
 * Cities: Skylines II UI Module
 *
 * Id: ${MOD.id}
 * Author: ${MOD.author}
 * Version: ${MOD.version}
 * Dependencies: ${MOD.dependencies.join(",")}
`;

module.exports = {
  mode: "production",
  stats: "errors-warnings",
  entry: {
    [MOD.id]: "./src/index.tsx",
  },
  // The game provides React and its UI API as globals; they must not be bundled.
  externalsType: "window",
  externals: {
    react: "React",
    "react-dom": "ReactDOM",
    "cs2/modding": "cs2/modding",
    "cs2/api": "cs2/api",
    "cs2/bindings": "cs2/bindings",
    "cs2/l10n": "cs2/l10n",
    "cs2/ui": "cs2/ui",
    "cs2/input": "cs2/input",
    "cs2/utils": "cs2/utils",
    "cohtml/cohtml": "cohtml/cohtml",
  },
  module: {
    rules: [
      {
        test: /\.tsx?$/,
        use: "ts-loader",
        exclude: /node_modules/,
      },
      {
        test: /\.s?css$/,
        include: path.join(__dirname, "src"),
        use: [
          MiniCssExtractPlugin.loader,
          {
            loader: "css-loader",
            options: {
              url: true,
              importLoaders: 1,
              modules: {
                auto: true,
                exportLocalsConvention: "camelCase",
                localIdentName: "st_[local]_[hash:base64:3]",
              },
            },
          },
          "sass-loader",
        ],
      },
      {
        test: /\.(png|jpe?g|gif|svg)$/i,
        type: "asset/resource",
        generator: {
          filename: "images/[name][ext][query]",
        },
      },
    ],
  },
  resolve: {
    extensions: [".tsx", ".ts", ".js"],
    modules: ["node_modules", path.join(__dirname, "src")],
    alias: {
      "mod.json": path.resolve(__dirname, "mod.json"),
    },
  },
  output: {
    path: path.resolve(__dirname, OUTPUT_DIR),
    library: {
      type: "module",
    },
    publicPath: "coui://ui-mods/",
  },
  optimization: {
    minimize: true,
    minimizer: [
      new TerserPlugin({
        extractComments: {
          banner: () => banner,
        },
      }),
    ],
  },
  experiments: {
    outputModule: true,
  },
  plugins: [new MiniCssExtractPlugin(), new CSSPresencePlugin()],
};
