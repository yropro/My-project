#ifndef LEVIATHAN_VOID_SECTOR_CLIP
#define LEVIATHAN_VOID_SECTOR_CLIP
float4 _Sector;
void ClipVoidSector(float2 position)
{
    if (_Sector.w < 0.5) return;
    clip(_Sector.z - length(position));
    if (_Sector.y >= 6.28318 || dot(position, position) < 0.00000001) return;
    float angle = atan2(position.y, position.x) - _Sector.x;
    angle = frac(angle / 6.28318530718) * 6.28318530718;
    clip(_Sector.y - angle);
}
#endif
