# 图标与品牌资源

`Resources/AIMaster.png` 是 AIMaster 图标原图，`Resources/AIMaster.ico` 包含 16、24、32、48、64、128 和 256 px 图层，供 Windows 可执行文件和资源管理器使用。

图标由 OpenAI 内置 imagegen 工具生成，已采用新的几何 A 字标。青绿渐变的上升折带代表清晰的进度与掌控，深翡翠色圆角底增强小尺寸辨识度，底板外部保持透明。PNG 使用最终清理版本；ICO 仅进行 Windows 格式与多尺寸封装，不改变图案。

主窗口、悬浮窗、系统托盘和可执行文件均沿用 `Resources/AIMaster.ico`；中英文 README 沿用 `Resources/AIMaster.png`。替换原路径资源即可让后续构建使用新图标。

生成提示词：

```text
Use case: logo-brand
Asset type: AI-Master Windows app and promotional web icon
Primary request: A bold geometric A monogram made from a folded ascending ribbon, with a triangular negative-space counter and a clean crossbar.
Style/medium: precise vector-like edges, almost-flat matte app tile, contemporary developer-tool identity
Composition/framing: one centered standalone rounded-square icon, balanced mass, thick silhouette legible at small sizes
Color palette: cyan-to-mint gradient on the A, smooth deep emerald-black tile
Constraints: real transparency outside the tile; no brain, circuit nodes, gauge ring, captions, watermark, external glow or shadow, 3D or mockup
Final cleanup: remove all background scribbles, mottling and patches; preserve the A shape, size, position, gradient, tile contour and outside alpha
```
